using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Extensions;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.GameDesign.UX_UI_Design.HUD_Elements;
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

namespace NEDA.GameDesign.UX_UI_Design.InterfaceDesign;
{
    /// <summary>
    /// Layout Manager - Manages UI layouts, positioning, and responsive design;
    /// </summary>
    public class LayoutManager : IDisposable, INotifyPropertyChanged;
    {
        #region Properties and Fields;

        private readonly ILogger _logger;
        private readonly ThemeManager _themeManager;
        private readonly ProjectManager _projectManager;
        private readonly DisplaySystem _displaySystem;

        private readonly Dictionary<string, UILayout> _layouts;
        private readonly Dictionary<string, UIElement> _elements;
        private readonly Dictionary<string, LayoutConstraint> _constraints;
        private readonly Dictionary<string, LayoutTemplate> _templates;

        private LayoutConfiguration _configuration;
        private LayoutMetrics _metrics;
        private LayoutState _currentState;
        private ScreenSize _currentScreenSize;
        private Orientation _currentOrientation;
        private string _activeLayoutName;
        private bool _isInitialized;
        private readonly object _syncLock = new object();
        private readonly Queue<LayoutCommand> _commandQueue;

        private UILayout _activeLayout;
        private DateTime _lastUpdateTime;
        private TimeSpan _totalUptime;
        private LayoutStatistics _currentStatistics;

        // Property change notifications;
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Current layout state;
        /// </summary>
        public LayoutState CurrentState;
        {
            get => _currentState;
            private set;
            {
                if (_currentState != value)
                {
                    _currentState = value;
                    OnPropertyChanged();
                    OnLayoutStateChanged(new LayoutStateEventArgs;
                    {
                        PreviousState = _currentState,
                        NewState = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Current screen size;
        /// </summary>
        public ScreenSize CurrentScreenSize;
        {
            get => _currentScreenSize;
            set;
            {
                if (_currentScreenSize != value)
                {
                    _currentScreenSize = value;
                    OnPropertyChanged();
                    OnScreenSizeChanged(new ScreenSizeEventArgs;
                    {
                        ScreenSize = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Current orientation;
        /// </summary>
        public Orientation CurrentOrientation;
        {
            get => _currentOrientation;
            set;
            {
                if (_currentOrientation != value)
                {
                    _currentOrientation = value;
                    OnPropertyChanged();
                    OnOrientationChanged(new OrientationEventArgs;
                    {
                        Orientation = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Active layout;
        /// </summary>
        public UILayout ActiveLayout;
        {
            get => _activeLayout;
            set;
            {
                if (_activeLayout != value)
                {
                    _activeLayout = value;
                    OnPropertyChanged();
                    OnActiveLayoutChanged(new LayoutEventArgs;
                    {
                        Layout = value,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
        }

        /// <summary>
        /// Collection of available layouts;
        /// </summary>
        public ReadOnlyDictionary<string, UILayout> Layouts => new ReadOnlyDictionary<string, UILayout>(_layouts);

        /// <summary>
        /// Collection of UI elements;
        /// </summary>
        public ReadOnlyDictionary<string, UIElement> Elements => new ReadOnlyDictionary<string, UIElement>(_elements);

        /// <summary>
        /// Collection of layout constraints;
        /// </summary>
        public ReadOnlyDictionary<string, LayoutConstraint> Constraints => new ReadOnlyDictionary<string, LayoutConstraint>(_constraints);

        /// <summary>
        /// Collection of layout templates;
        /// </summary>
        public ReadOnlyDictionary<string, LayoutTemplate> Templates => new ReadOnlyDictionary<string, LayoutTemplate>(_templates);

        /// <summary>
        /// Total system uptime;
        /// </summary>
        public TimeSpan TotalUptime => _totalUptime;

        /// <summary>
        /// Gets the total number of registered layouts;
        /// </summary>
        public int TotalLayoutCount => _layouts.Count;

        /// <summary>
        /// Gets the total number of UI elements;
        /// </summary>
        public int TotalElementCount => _elements.Count;

        /// <summary>
        /// Gets the total number of active constraints;
        /// </summary>
        public int TotalConstraintCount => _constraints.Count;

        /// <summary>
        /// Layout metrics and statistics;
        /// </summary>
        public LayoutMetrics Metrics => _metrics;

        /// <summary>
        /// Current layout statistics;
        /// </summary>
        public LayoutStatistics CurrentStatistics => _currentStatistics;

        #endregion;

        #region Events;

        public event EventHandler<LayoutStateEventArgs> LayoutStateChanged;
        public event EventHandler<ScreenSizeEventArgs> ScreenSizeChanged;
        public event EventHandler<OrientationEventArgs> OrientationChanged;
        public event EventHandler<LayoutEventArgs> ActiveLayoutChanged;
        public event EventHandler<ElementEventArgs> ElementAdded;
        public event EventHandler<ElementEventArgs> ElementRemoved;
        public event EventHandler<ElementEventArgs> ElementUpdated;
        public event EventHandler<ConstraintEventArgs> ConstraintAdded;
        public event EventHandler<ConstraintEventArgs> ConstraintRemoved;
        public event EventHandler<TemplateEventArgs> TemplateCreated;
        public event EventHandler<TemplateEventArgs> TemplateApplied;
        public event EventHandler<LayoutErrorEventArgs> LayoutError;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of LayoutManager;
        /// </summary>
        public LayoutManager(
            ILogger logger,
            ThemeManager themeManager,
            ProjectManager projectManager,
            DisplaySystem displaySystem)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            _displaySystem = displaySystem ?? throw new ArgumentNullException(nameof(displaySystem));

            _layouts = new Dictionary<string, UILayout>();
            _elements = new Dictionary<string, UIElement>();
            _constraints = new Dictionary<string, LayoutConstraint>();
            _templates = new Dictionary<string, LayoutTemplate>();

            _commandQueue = new Queue<LayoutCommand>();
            _configuration = new LayoutConfiguration();
            _metrics = new LayoutMetrics();
            _currentState = LayoutState.Uninitialized;
            _currentScreenSize = ScreenSize.Default;
            _currentOrientation = Orientation.Landscape;
            _activeLayoutName = "Default";
            _isInitialized = false;

            _activeLayout = null;
            _lastUpdateTime = DateTime.UtcNow;
            _totalUptime = TimeSpan.Zero;
            _currentStatistics = new LayoutStatistics();

            InitializeEventHandlers();
            _logger.LogInformation("LayoutManager initialized successfully.");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Initializes the layout manager with configuration;
        /// </summary>
        public async Task InitializeAsync(LayoutConfiguration configuration)
        {
            ValidateConfiguration(configuration);

            try
            {
                CurrentState = LayoutState.Initializing;

                _configuration = configuration;
                _currentScreenSize = configuration.DefaultScreenSize;
                _currentOrientation = configuration.DefaultOrientation;

                // Initialize default layouts;
                await InitializeDefaultLayoutsAsync();

                // Load user layouts if available;
                await LoadUserLayoutsAsync();

                // Initialize default templates;
                await InitializeDefaultTemplatesAsync();

                // Apply active layout;
                await ApplyLayoutAsync(_activeLayoutName);

                // Start update loop;
                StartUpdateLoop();

                _isInitialized = true;
                CurrentState = LayoutState.Ready;

                _logger.LogInformation($"LayoutManager initialized with screen size: {_currentScreenSize}");
            }
            catch (Exception ex)
            {
                CurrentState = LayoutState.Error;
                _logger.LogError(ex, "Failed to initialize LayoutManager");
                throw new LayoutManagerException("Failed to initialize LayoutManager", ex);
            }
        }

        /// <summary>
        /// Creates a new UI layout;
        /// </summary>
        public async Task<UILayout> CreateLayoutAsync(string layoutName, LayoutType type, LayoutConfiguration config)
        {
            ValidateLayoutName(layoutName);
            ValidateLayoutConfiguration(config);

            try
            {
                if (_layouts.ContainsKey(layoutName))
                    throw new InvalidOperationException($"Layout with name '{layoutName}' already exists");

                var layout = new UILayout;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = layoutName,
                    Type = type,
                    Configuration = config,
                    ScreenSize = config.ScreenSize,
                    Orientation = config.Orientation,
                    IsActive = false,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                    Elements = new Dictionary<string, UIElement>(),
                    Constraints = new Dictionary<string, LayoutConstraint>()
                };

                lock (_syncLock)
                {
                    _layouts.Add(layoutName, layout);
                }

                UpdateMetrics();

                _logger.LogInformation($"Layout created: {layoutName} ({type})");
                return layout;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create layout: {layoutName}");
                throw new LayoutManagerException($"Failed to create layout: {layoutName}", ex);
            }
        }

        /// <summary>
        /// Applies a layout by name;
        /// </summary>
        public async Task ApplyLayoutAsync(string layoutName)
        {
            ValidateLayoutName(layoutName);

            try
            {
                if (!_layouts.TryGetValue(layoutName, out var layout))
                    throw new KeyNotFoundException($"Layout not found: {layoutName}");

                // Deactivate current layout;
                if (ActiveLayout != null)
                {
                    await DeactivateLayoutAsync(ActiveLayout.Name);
                }

                // Clear current elements and constraints;
                lock (_syncLock)
                {
                    _elements.Clear();
                    _constraints.Clear();
                }

                // Apply layout elements;
                foreach (var element in layout.Elements)
                {
                    await AddElementInternalAsync(element.Value);
                }

                // Apply layout constraints;
                foreach (var constraint in layout.Constraints)
                {
                    await AddConstraintInternalAsync(constraint.Value);
                }

                // Set as active;
                layout.IsActive = true;
                layout.LastApplied = DateTime.UtcNow;
                _activeLayoutName = layoutName;
                ActiveLayout = layout;

                // Apply responsive adjustments;
                await ApplyResponsiveAdjustmentsAsync();

                _logger.LogInformation($"Layout applied: {layoutName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply layout: {layoutName}");
                throw new LayoutManagerException($"Failed to apply layout: {layoutName}", ex);
            }
        }

        /// <summary>
        /// Adds a UI element to the current layout;
        /// </summary>
        public async Task<UIElement> AddElementAsync(string elementId, ElementType type, ElementConfiguration config)
        {
            ValidateElementId(elementId);
            ValidateElementConfiguration(config);

            try
            {
                if (_elements.ContainsKey(elementId))
                    throw new InvalidOperationException($"Element with ID '{elementId}' already exists");

                var element = new UIElement;
                {
                    Id = elementId,
                    Type = type,
                    Configuration = config,
                    Position = config.Position,
                    Size = config.Size,
                    Anchor = config.Anchor,
                    Margin = config.Margin,
                    Padding = config.Padding,
                    IsVisible = config.IsVisible,
                    ZIndex = config.ZIndex,
                    CreatedTime = DateTime.UtcNow,
                    LastUpdated = DateTime.UtcNow;
                };

                // Apply responsive sizing;
                await ApplyResponsiveSizingAsync(element);

                await AddElementInternalAsync(element);

                UpdateMetrics();

                OnElementAdded(new ElementEventArgs;
                {
                    Element = element,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Element added: {elementId} ({type})");
                return element;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add element: {elementId}");
                throw new LayoutManagerException($"Failed to add element: {elementId}", ex);
            }
        }

        /// <summary>
        /// Removes a UI element from the current layout;
        /// </summary>
        public async Task<bool> RemoveElementAsync(string elementId)
        {
            ValidateElementId(elementId);

            try
            {
                if (!_elements.TryGetValue(elementId, out var element))
                    return false;

                // Remove associated constraints;
                var constraintsToRemove = _constraints.Values;
                    .Where(c => c.TargetElementId == elementId || c.SourceElementId == elementId)
                    .ToList();

                foreach (var constraint in constraintsToRemove)
                {
                    await RemoveConstraintAsync(constraint.Id);
                }

                lock (_syncLock)
                {
                    _elements.Remove(elementId);
                }

                // Remove from active layout;
                if (ActiveLayout != null && ActiveLayout.Elements.ContainsKey(elementId))
                {
                    ActiveLayout.Elements.Remove(elementId);
                    ActiveLayout.ModifiedDate = DateTime.UtcNow;
                }

                UpdateMetrics();

                OnElementRemoved(new ElementEventArgs;
                {
                    Element = element,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Element removed: {elementId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove element: {elementId}");
                throw new LayoutManagerException($"Failed to remove element: {elementId}", ex);
            }
        }

        /// <summary>
        /// Updates a UI element;
        /// </summary>
        public async Task UpdateElementAsync(string elementId, ElementUpdate update)
        {
            ValidateElementId(elementId);
            ValidateElementUpdate(update);

            try
            {
                if (!_elements.TryGetValue(elementId, out var element))
                    throw new KeyNotFoundException($"Element not found: {elementId}");

                // Apply updates;
                if (update.Position.HasValue)
                    element.Position = update.Position.Value;

                if (update.Size.HasValue)
                    element.Size = update.Size.Value;

                if (update.Anchor.HasValue)
                    element.Anchor = update.Anchor.Value;

                if (update.Margin.HasValue)
                    element.Margin = update.Margin.Value;

                if (update.Padding.HasValue)
                    element.Padding = update.Padding.Value;

                if (update.IsVisible.HasValue)
                    element.IsVisible = update.IsVisible.Value;

                if (update.ZIndex.HasValue)
                    element.ZIndex = update.ZIndex.Value;

                if (update.Opacity.HasValue)
                    element.Opacity = update.Opacity.Value;

                element.LastUpdated = DateTime.UtcNow;

                // Apply responsive adjustments;
                await ApplyResponsiveSizingAsync(element);

                // Update constraints if needed;
                await UpdateElementConstraintsAsync(elementId);

                OnElementUpdated(new ElementEventArgs;
                {
                    Element = element,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Element updated: {elementId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update element: {elementId}");
                throw new LayoutManagerException($"Failed to update element: {elementId}", ex);
            }
        }

        /// <summary>
        /// Adds a layout constraint;
        /// </summary>
        public async Task<LayoutConstraint> AddConstraintAsync(LayoutConstraint constraint)
        {
            ValidateConstraint(constraint);

            try
            {
                if (_constraints.ContainsKey(constraint.Id))
                    throw new InvalidOperationException($"Constraint with ID '{constraint.Id}' already exists");

                // Validate constraint references;
                if (!string.IsNullOrEmpty(constraint.SourceElementId) &&
                    !_elements.ContainsKey(constraint.SourceElementId))
                    throw new KeyNotFoundException($"Source element not found: {constraint.SourceElementId}");

                if (!string.IsNullOrEmpty(constraint.TargetElementId) &&
                    !_elements.ContainsKey(constraint.TargetElementId))
                    throw new KeyNotFoundException($"Target element not found: {constraint.TargetElementId}");

                constraint.CreatedTime = DateTime.UtcNow;
                constraint.LastApplied = DateTime.UtcNow;

                lock (_syncLock)
                {
                    _constraints.Add(constraint.Id, constraint);
                }

                // Apply constraint;
                await ApplyConstraintAsync(constraint);

                UpdateMetrics();

                OnConstraintAdded(new ConstraintEventArgs;
                {
                    Constraint = constraint,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Constraint added: {constraint.Id} ({constraint.Type})");
                return constraint;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to add constraint: {constraint.Id}");
                throw new LayoutManagerException($"Failed to add constraint: {constraint.Id}", ex);
            }
        }

        /// <summary>
        /// Removes a layout constraint;
        /// </summary>
        public async Task<bool> RemoveConstraintAsync(string constraintId)
        {
            ValidateConstraintId(constraintId);

            try
            {
                if (!_constraints.TryGetValue(constraintId, out var constraint))
                    return false;

                // Remove constraint effects;
                await RemoveConstraintEffectsAsync(constraint);

                lock (_syncLock)
                {
                    _constraints.Remove(constraintId);
                }

                UpdateMetrics();

                OnConstraintRemoved(new ConstraintEventArgs;
                {
                    Constraint = constraint,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Constraint removed: {constraintId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to remove constraint: {constraintId}");
                throw new LayoutManagerException($"Failed to remove constraint: {constraintId}", ex);
            }
        }

        /// <summary>
        /// Creates a layout template;
        /// </summary>
        public async Task<LayoutTemplate> CreateTemplateAsync(string templateName, TemplateType type)
        {
            ValidateTemplateName(templateName);

            try
            {
                if (_templates.ContainsKey(templateName))
                    throw new InvalidOperationException($"Template with name '{templateName}' already exists");

                var template = new LayoutTemplate;
                {
                    Name = templateName,
                    Type = type,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                    Elements = new Dictionary<string, UIElement>(),
                    Constraints = new Dictionary<string, LayoutConstraint>(),
                    ScreenSizes = new List<ScreenSize> { ScreenSize.Default }
                };

                // Copy current layout elements if creating from current state;
                if (type == TemplateType.FromCurrent)
                {
                    foreach (var element in _elements)
                    {
                        template.Elements.Add(element.Key, element.Value.Clone());
                    }

                    foreach (var constraint in _constraints)
                    {
                        template.Constraints.Add(constraint.Key, constraint.Value.Clone());
                    }
                }

                lock (_syncLock)
                {
                    _templates.Add(templateName, template);
                }

                OnTemplateCreated(new TemplateEventArgs;
                {
                    Template = template,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Template created: {templateName} ({type})");
                return template;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create template: {templateName}");
                throw new LayoutManagerException($"Failed to create template: {templateName}", ex);
            }
        }

        /// <summary>
        /// Applies a layout template;
        /// </summary>
        public async Task ApplyTemplateAsync(string templateName)
        {
            ValidateTemplateName(templateName);

            try
            {
                if (!_templates.TryGetValue(templateName, out var template))
                    throw new KeyNotFoundException($"Template not found: {templateName}");

                // Clear current layout;
                await ClearLayoutAsync();

                // Apply template elements;
                foreach (var element in template.Elements)
                {
                    await AddElementInternalAsync(element.Value);
                }

                // Apply template constraints;
                foreach (var constraint in template.Constraints)
                {
                    await AddConstraintInternalAsync(constraint.Value);
                }

                OnTemplateApplied(new TemplateEventArgs;
                {
                    Template = template,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Template applied: {templateName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to apply template: {templateName}");
                throw new LayoutManagerException($"Failed to apply template: {templateName}", ex);
            }
        }

        /// <summary>
        /// Updates the screen size and orientation;
        /// </summary>
        public async Task UpdateScreenSizeAsync(ScreenSize screenSize, Orientation orientation)
        {
            ValidateScreenSize(screenSize);

            try
            {
                var previousScreenSize = CurrentScreenSize;
                var previousOrientation = CurrentOrientation;

                CurrentScreenSize = screenSize;
                CurrentOrientation = orientation;

                // Apply responsive adjustments;
                await ApplyResponsiveAdjustmentsAsync();

                // Update active layout if it exists;
                if (ActiveLayout != null)
                {
                    ActiveLayout.ScreenSize = screenSize;
                    ActiveLayout.Orientation = orientation;
                    ActiveLayout.ModifiedDate = DateTime.UtcNow;
                }

                _logger.LogInformation($"Screen size updated: {previousScreenSize}->{screenSize}, {previousOrientation}->{orientation}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update screen size: {screenSize}");
                throw new LayoutManagerException($"Failed to update screen size: {screenSize}", ex);
            }
        }

        /// <summary>
        /// Gets an element by ID;
        /// </summary>
        public UIElement GetElement(string elementId)
        {
            ValidateElementId(elementId);

            lock (_syncLock)
            {
                return _elements.TryGetValue(elementId, out var element)
                    ? element;
                    : throw new KeyNotFoundException($"Element not found: {elementId}");
            }
        }

        /// <summary>
        /// Gets elements by type;
        /// </summary>
        public IEnumerable<UIElement> GetElementsByType(ElementType type)
        {
            lock (_syncLock)
            {
                return _elements.Values.Where(e => e.Type == type).ToList();
            }
        }

        /// <summary>
        /// Gets elements in a specific area;
        /// </summary>
        public IEnumerable<UIElement> GetElementsInArea(Rectangle area)
        {
            lock (_syncLock)
            {
                return _elements.Values.Where(e =>
                    e.Position.X >= area.X &&
                    e.Position.X + e.Size.Width <= area.X + area.Width &&
                    e.Position.Y >= area.Y &&
                    e.Position.Y + e.Size.Height <= area.Y + area.Height).ToList();
            }
        }

        /// <summary>
        /// Calculates element bounds;
        /// </summary>
        public Rectangle CalculateElementBounds(string elementId)
        {
            var element = GetElement(elementId);

            var bounds = new Rectangle;
            {
                X = element.Position.X - element.Margin.Left,
                Y = element.Position.Y - element.Margin.Top,
                Width = element.Size.Width + element.Margin.Left + element.Margin.Right,
                Height = element.Size.Height + element.Margin.Top + element.Margin.Bottom;
            };

            return bounds;
        }

        /// <summary>
        /// Checks for element collisions;
        /// </summary>
        public IEnumerable<ElementCollision> CheckCollisions()
        {
            var collisions = new List<ElementCollision>();
            var elements = _elements.Values.ToList();

            for (int i = 0; i < elements.Count; i++)
            {
                for (int j = i + 1; j < elements.Count; j++)
                {
                    var bounds1 = CalculateElementBounds(elements[i].Id);
                    var bounds2 = CalculateElementBounds(elements[j].Id);

                    if (bounds1.Intersects(bounds2))
                    {
                        collisions.Add(new ElementCollision;
                        {
                            Element1 = elements[i],
                            Element2 = elements[j],
                            Intersection = bounds1.Intersection(bounds2),
                            Severity = CalculateCollisionSeverity(bounds1, bounds2)
                        });
                    }
                }
            }

            return collisions;
        }

        /// <summary>
        /// Clears the current layout;
        /// </summary>
        public async Task ClearLayoutAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    _elements.Clear();
                    _constraints.Clear();
                }

                if (ActiveLayout != null)
                {
                    ActiveLayout.Elements.Clear();
                    ActiveLayout.Constraints.Clear();
                    ActiveLayout.ModifiedDate = DateTime.UtcNow;
                }

                UpdateMetrics();

                _logger.LogInformation("Layout cleared");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clear layout");
                throw new LayoutManagerException("Failed to clear layout", ex);
            }
        }

        /// <summary>
        /// Updates the layout manager (to be called each frame)
        /// </summary>
        public async Task UpdateAsync(TimeSpan deltaTime)
        {
            if (!_isInitialized || CurrentState == LayoutState.Error)
                return;

            try
            {
                _totalUptime += deltaTime;

                // Process command queue;
                await ProcessCommandQueueAsync();

                // Update constraints;
                await UpdateConstraintsAsync(deltaTime);

                // Update animations;
                await UpdateAnimationsAsync(deltaTime);

                // Update statistics;
                UpdateStatistics(deltaTime);

                // Update metrics;
                UpdateMetrics();

                _lastUpdateTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during layout manager update");
                OnLayoutError(new LayoutErrorEventArgs;
                {
                    ErrorMessage = "Update error",
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Renders the layout;
        /// </summary>
        public async Task RenderAsync()
        {
            if (!_isInitialized || CurrentState != LayoutState.Active)
                return;

            try
            {
                // Get elements sorted by Z-index;
                var elementsToRender = GetSortedElementsForRendering();

                // Render each element;
                foreach (var element in elementsToRender)
                {
                    if (element.IsVisible)
                    {
                        await RenderElementAsync(element);
                    }
                }

                _currentStatistics.FramesRendered++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during layout render");
                OnLayoutError(new LayoutErrorEventArgs;
                {
                    ErrorMessage = "Render error",
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Gets layout manager statistics;
        /// </summary>
        public LayoutManagerStatistics GetStatistics()
        {
            return new LayoutManagerStatistics;
            {
                TotalLayouts = _layouts.Count,
                TotalElements = _elements.Count,
                TotalConstraints = _constraints.Count,
                TotalTemplates = _templates.Count,
                Uptime = _totalUptime,
                State = CurrentState,
                ScreenSize = CurrentScreenSize,
                Orientation = CurrentOrientation,
                Metrics = _metrics,
                LayoutStatistics = _currentStatistics,
                LastUpdate = _lastUpdateTime;
            };
        }

        #endregion;

        #region Private Methods;

        private async Task InitializeDefaultLayoutsAsync()
        {
            // Create default layouts;
            var defaultLayout = new UILayout;
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Default",
                Type = LayoutType.Standard,
                Configuration = new LayoutConfiguration(),
                ScreenSize = ScreenSize.Default,
                Orientation = Orientation.Landscape,
                IsActive = false,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Elements = new Dictionary<string, UIElement>(),
                Constraints = new Dictionary<string, LayoutConstraint>()
            };

            var mobileLayout = new UILayout;
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Mobile",
                Type = LayoutType.Responsive,
                Configuration = new LayoutConfiguration;
                {
                    ScreenSize = ScreenSize.Mobile,
                    Orientation = Orientation.Portrait;
                },
                ScreenSize = ScreenSize.Mobile,
                Orientation = Orientation.Portrait,
                IsActive = false,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Elements = new Dictionary<string, UIElement>(),
                Constraints = new Dictionary<string, LayoutConstraint>()
            };

            lock (_syncLock)
            {
                _layouts.Add("Default", defaultLayout);
                _layouts.Add("Mobile", mobileLayout);
            }

            _activeLayoutName = "Default";

            _logger.LogInformation($"Initialized {_layouts.Count} default layouts");
        }

        private async Task LoadUserLayoutsAsync()
        {
            try
            {
                var userLayouts = await _projectManager.LoadUILayoutsAsync();

                foreach (var layout in userLayouts)
                {
                    if (!_layouts.ContainsKey(layout.Name))
                    {
                        _layouts.Add(layout.Name, layout);
                    }
                }

                _logger.LogInformation($"Loaded {userLayouts.Count} user layouts");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load user layouts");
            }
        }

        private async Task InitializeDefaultTemplatesAsync()
        {
            // Create default templates;
            var gridTemplate = new LayoutTemplate;
            {
                Name = "Grid",
                Type = TemplateType.Grid,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Elements = new Dictionary<string, UIElement>(),
                Constraints = new Dictionary<string, LayoutConstraint>(),
                ScreenSizes = new List<ScreenSize> { ScreenSize.Default, ScreenSize.Tablet, ScreenSize.Mobile }
            };

            var sidebarTemplate = new LayoutTemplate;
            {
                Name = "Sidebar",
                Type = TemplateType.Sidebar,
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow,
                Elements = new Dictionary<string, UIElement>(),
                Constraints = new Dictionary<string, LayoutConstraint>(),
                ScreenSizes = new List<ScreenSize> { ScreenSize.Default, ScreenSize.Wide }
            };

            lock (_syncLock)
            {
                _templates.Add("Grid", gridTemplate);
                _templates.Add("Sidebar", sidebarTemplate);
            }

            _logger.LogInformation($"Initialized {_templates.Count} default templates");
        }

        private void StartUpdateLoop()
        {
            // This would typically be integrated with the game/application main loop;
            _logger.LogInformation("LayoutManager update loop started");
        }

        private async Task AddElementInternalAsync(UIElement element)
        {
            lock (_syncLock)
            {
                _elements.Add(element.Id, element);
            }

            // Add to active layout;
            if (ActiveLayout != null && !ActiveLayout.Elements.ContainsKey(element.Id))
            {
                ActiveLayout.Elements.Add(element.Id, element.Clone());
                ActiveLayout.ModifiedDate = DateTime.UtcNow;
            }

            await Task.CompletedTask;
        }

        private async Task AddConstraintInternalAsync(LayoutConstraint constraint)
        {
            lock (_syncLock)
            {
                _constraints.Add(constraint.Id, constraint);
            }

            // Add to active layout;
            if (ActiveLayout != null && !ActiveLayout.Constraints.ContainsKey(constraint.Id))
            {
                ActiveLayout.Constraints.Add(constraint.Id, constraint.Clone());
                ActiveLayout.ModifiedDate = DateTime.UtcNow;
            }

            await Task.CompletedTask;
        }

        private async Task ApplyConstraintAsync(LayoutConstraint constraint)
        {
            switch (constraint.Type)
            {
                case ConstraintType.Alignment:
                    await ApplyAlignmentConstraintAsync(constraint);
                    break;

                case ConstraintType.Spacing:
                    await ApplySpacingConstraintAsync(constraint);
                    break;

                case ConstraintType.Size:
                    await ApplySizeConstraintAsync(constraint);
                    break;

                case ConstraintType.Position:
                    await ApplyPositionConstraintAsync(constraint);
                    break;

                case ConstraintType.Proportional:
                    await ApplyProportionalConstraintAsync(constraint);
                    break;

                default:
                    _logger.LogWarning($"Unknown constraint type: {constraint.Type}");
                    break;
            }

            constraint.LastApplied = DateTime.UtcNow;
        }

        private async Task ApplyAlignmentConstraintAsync(LayoutConstraint constraint)
        {
            if (!_elements.TryGetValue(constraint.TargetElementId, out var targetElement))
                return;

            UIElement sourceElement = null;
            if (!string.IsNullOrEmpty(constraint.SourceElementId))
            {
                _elements.TryGetValue(constraint.SourceElementId, out sourceElement);
            }

            switch (constraint.Alignment)
            {
                case Alignment.Top:
                    targetElement.Position = new Point(targetElement.Position.X, 0);
                    break;

                case Alignment.Bottom:
                    if (sourceElement != null)
                    {
                        targetElement.Position = new Point(
                            targetElement.Position.X,
                            sourceElement.Position.Y + sourceElement.Size.Height + constraint.Value);
                    }
                    break;

                case Alignment.Left:
                    targetElement.Position = new Point(0, targetElement.Position.Y);
                    break;

                case Alignment.Right:
                    if (sourceElement != null)
                    {
                        targetElement.Position = new Point(
                            sourceElement.Position.X + sourceElement.Size.Width + constraint.Value,
                            targetElement.Position.Y);
                    }
                    break;

                case Alignment.CenterHorizontal:
                    if (sourceElement != null)
                    {
                        targetElement.Position = new Point(
                            sourceElement.Position.X + (sourceElement.Size.Width - targetElement.Size.Width) / 2,
                            targetElement.Position.Y);
                    }
                    break;

                case Alignment.CenterVertical:
                    if (sourceElement != null)
                    {
                        targetElement.Position = new Point(
                            targetElement.Position.X,
                            sourceElement.Position.Y + (sourceElement.Size.Height - targetElement.Size.Height) / 2);
                    }
                    break;
            }

            await UpdateElementAsync(targetElement.Id, new ElementUpdate;
            {
                Position = targetElement.Position;
            });
        }

        private async Task ApplySpacingConstraintAsync(LayoutConstraint constraint)
        {
            if (!_elements.TryGetValue(constraint.TargetElementId, out var targetElement) ||
                !_elements.TryGetValue(constraint.SourceElementId, out var sourceElement))
                return;

            var direction = constraint.Parameters.TryGetValue("direction", out var dirValue)
                ? (Direction)dirValue;
                : Direction.Right;

            switch (direction)
            {
                case Direction.Right:
                    targetElement.Position = new Point(
                        sourceElement.Position.X + sourceElement.Size.Width + constraint.Value,
                        targetElement.Position.Y);
                    break;

                case Direction.Left:
                    targetElement.Position = new Point(
                        sourceElement.Position.X - targetElement.Size.Width - constraint.Value,
                        targetElement.Position.Y);
                    break;

                case Direction.Down:
                    targetElement.Position = new Point(
                        targetElement.Position.X,
                        sourceElement.Position.Y + sourceElement.Size.Height + constraint.Value);
                    break;

                case Direction.Up:
                    targetElement.Position = new Point(
                        targetElement.Position.X,
                        sourceElement.Position.Y - targetElement.Size.Height - constraint.Value);
                    break;
            }

            await UpdateElementAsync(targetElement.Id, new ElementUpdate;
            {
                Position = targetElement.Position;
            });
        }

        private async Task ApplySizeConstraintAsync(LayoutConstraint constraint)
        {
            if (!_elements.TryGetValue(constraint.TargetElementId, out var targetElement))
                return;

            var dimension = constraint.Parameters.TryGetValue("dimension", out var dimValue)
                ? (Dimension)dimValue;
                : Dimension.Width;

            switch (dimension)
            {
                case Dimension.Width:
                    targetElement.Size = new Size(constraint.Value, targetElement.Size.Height);
                    break;

                case Dimension.Height:
                    targetElement.Size = new Size(targetElement.Size.Width, constraint.Value);
                    break;

                case Dimension.Both:
                    targetElement.Size = new Size(constraint.Value, constraint.Value);
                    break;
            }

            await UpdateElementAsync(targetElement.Id, new ElementUpdate;
            {
                Size = targetElement.Size;
            });
        }

        private async Task ApplyPositionConstraintAsync(LayoutConstraint constraint)
        {
            if (!_elements.TryGetValue(constraint.TargetElementId, out var targetElement))
                return;

            var axis = constraint.Parameters.TryGetValue("axis", out var axisValue)
                ? (Axis)axisValue;
                : Axis.X;

            switch (axis)
            {
                case Axis.X:
                    targetElement.Position = new Point(constraint.Value, targetElement.Position.Y);
                    break;

                case Axis.Y:
                    targetElement.Position = new Point(targetElement.Position.X, constraint.Value);
                    break;
            }

            await UpdateElementAsync(targetElement.Id, new ElementUpdate;
            {
                Position = targetElement.Position;
            });
        }

        private async Task ApplyProportionalConstraintAsync(LayoutConstraint constraint)
        {
            if (!_elements.TryGetValue(constraint.TargetElementId, out var targetElement))
                return;

            var property = constraint.Parameters.TryGetValue("property", out var propValue)
                ? (string)propValue;
                : "width";

            var reference = constraint.Parameters.TryGetValue("reference", out var refValue)
                ? (float)refValue;
                : 1.0f;

            var value = reference * constraint.Value;

            switch (property.ToLower())
            {
                case "width":
                    targetElement.Size = new Size(value, targetElement.Size.Height);
                    break;

                case "height":
                    targetElement.Size = new Size(targetElement.Size.Width, value);
                    break;

                case "x":
                    targetElement.Position = new Point(value, targetElement.Position.Y);
                    break;

                case "y":
                    targetElement.Position = new Point(targetElement.Position.X, value);
                    break;
            }

            await UpdateElementAsync(targetElement.Id, new ElementUpdate;
            {
                Position = targetElement.Position,
                Size = targetElement.Size;
            });
        }

        private async Task RemoveConstraintEffectsAsync(LayoutConstraint constraint)
        {
            // Reset elements affected by this constraint;
            // This would typically involve recalculating positions/sizes based on other constraints;

            await Task.CompletedTask;
        }

        private async Task UpdateElementConstraintsAsync(string elementId)
        {
            // Find all constraints affecting this element;
            var constraints = _constraints.Values;
                .Where(c => c.TargetElementId == elementId || c.SourceElementId == elementId)
                .ToList();

            // Re-apply constraints;
            foreach (var constraint in constraints)
            {
                await ApplyConstraintAsync(constraint);
            }
        }

        private async Task ApplyResponsiveSizingAsync(UIElement element)
        {
            var responsiveConfig = element.Configuration.ResponsiveSettings;

            if (responsiveConfig == null)
                return;

            // Apply responsive sizing based on screen size;
            Size newSize = element.Size;

            switch (CurrentScreenSize)
            {
                case ScreenSize.Mobile:
                    newSize = responsiveConfig.MobileSize ?? element.Size;
                    break;

                case ScreenSize.Tablet:
                    newSize = responsiveConfig.TabletSize ?? element.Size;
                    break;

                case ScreenSize.Desktop:
                    newSize = responsiveConfig.DesktopSize ?? element.Size;
                    break;

                case ScreenSize.Wide:
                    newSize = responsiveConfig.WideSize ?? element.Size;
                    break;
            }

            // Apply responsive positioning;
            Point newPosition = element.Position;

            switch (CurrentOrientation)
            {
                case Orientation.Portrait:
                    newPosition = responsiveConfig.PortraitPosition ?? element.Position;
                    break;

                case Orientation.Landscape:
                    newPosition = responsiveConfig.LandscapePosition ?? element.Position;
                    break;
            }

            // Apply updates if changed;
            if (newSize != element.Size || newPosition != element.Position)
            {
                element.Size = newSize;
                element.Position = newPosition;
                element.LastUpdated = DateTime.UtcNow;
            }

            await Task.CompletedTask;
        }

        private async Task ApplyResponsiveAdjustmentsAsync()
        {
            // Apply responsive adjustments to all elements;
            foreach (var element in _elements.Values)
            {
                await ApplyResponsiveSizingAsync(element);
            }

            // Re-apply all constraints;
            foreach (var constraint in _constraints.Values)
            {
                await ApplyConstraintAsync(constraint);
            }

            _logger.LogDebug("Applied responsive adjustments");
        }

        private async Task DeactivateLayoutAsync(string layoutName)
        {
            if (!_layouts.TryGetValue(layoutName, out var layout))
                return;

            layout.IsActive = false;
            layout.LastDeactivated = DateTime.UtcNow;

            await Task.CompletedTask;
        }

        private CollisionSeverity CalculateCollisionSeverity(Rectangle bounds1, Rectangle bounds2)
        {
            var intersection = bounds1.Intersection(bounds2);
            var area1 = bounds1.Area;
            var area2 = bounds2.Area;
            var intersectionArea = intersection.Area;

            var severityPercentage = (intersectionArea / Math.Min(area1, area2)) * 100;

            if (severityPercentage > 50)
                return CollisionSeverity.High;
            else if (severityPercentage > 25)
                return CollisionSeverity.Medium;
            else;
                return CollisionSeverity.Low;
        }

        private IEnumerable<UIElement> GetSortedElementsForRendering()
        {
            lock (_syncLock)
            {
                return _elements.Values;
                    .OrderBy(e => e.ZIndex)
                    .ThenBy(e => e.CreatedTime)
                    .ToList();
            }
        }

        private async Task RenderElementAsync(UIElement element)
        {
            // This would integrate with the actual rendering system;
            // For now, we'll just update statistics;

            element.LastRendered = DateTime.UtcNow;
            _currentStatistics.ElementsRendered++;

            // Create display widget if needed;
            if (element.Configuration.CreateDisplayWidget)
            {
                await _displaySystem.AddWidgetAsync(
                    $"layout_{element.Id}",
                    ConvertElementTypeToWidgetType(element.Type),
                    new WidgetConfiguration;
                    {
                        Position = new Vector2(element.Position.X, element.Position.Y),
                        Size = new Vector2(element.Size.Width, element.Size.Height),
                        IsVisible = element.IsVisible,
                        Layer = element.Configuration.Layer,
                        ZIndex = element.ZIndex;
                    });
            }

            await Task.CompletedTask;
        }

        private WidgetType ConvertElementTypeToWidgetType(ElementType elementType)
        {
            return elementType switch;
            {
                ElementType.Button => WidgetType.Custom,
                ElementType.Panel => WidgetType.Custom,
                ElementType.Text => WidgetType.Custom,
                ElementType.Image => WidgetType.Custom,
                ElementType.Input => WidgetType.Custom,
                ElementType.Container => WidgetType.Custom,
                _ => WidgetType.Custom;
            };
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

        private void ProcessCommand(LayoutCommand command)
        {
            switch (command.Type)
            {
                case LayoutCommandType.AddElement:
                    _ = AddElementAsync(
                        command.Parameters["elementId"] as string,
                        (ElementType)command.Parameters["type"],
                        (ElementConfiguration)command.Parameters["config"]);
                    break;

                case LayoutCommandType.RemoveElement:
                    _ = RemoveElementAsync(command.Parameters["elementId"] as string);
                    break;

                case LayoutCommandType.UpdateElement:
                    _ = UpdateElementAsync(
                        command.Parameters["elementId"] as string,
                        (ElementUpdate)command.Parameters["update"]);
                    break;

                case LayoutCommandType.ApplyLayout:
                    _ = ApplyLayoutAsync(command.Parameters["layoutName"] as string);
                    break;

                case LayoutCommandType.ApplyTemplate:
                    _ = ApplyTemplateAsync(command.Parameters["templateName"] as string);
                    break;

                default:
                    _logger.LogWarning($"Unknown command type: {command.Type}");
                    break;
            }
        }

        private async Task UpdateConstraintsAsync(TimeSpan deltaTime)
        {
            // Update constraint timing and check for violations;
            foreach (var constraint in _constraints.Values)
            {
                constraint.UpdateTime += deltaTime;

                // Check constraint satisfaction;
                if (constraint.CheckInterval > TimeSpan.Zero &&
                    constraint.UpdateTime >= constraint.CheckInterval)
                {
                    await CheckConstraintSatisfactionAsync(constraint);
                    constraint.UpdateTime = TimeSpan.Zero;
                }
            }

            await Task.CompletedTask;
        }

        private async Task CheckConstraintSatisfactionAsync(LayoutConstraint constraint)
        {
            // Implement constraint satisfaction checking;
            // This would verify that the constraint is still being satisfied;

            bool isSatisfied = true; // Placeholder;

            if (!isSatisfied && constraint.Enforcement == ConstraintEnforcement.Strict)
            {
                _logger.LogWarning($"Constraint violation: {constraint.Id}");

                // Attempt to re-apply constraint;
                await ApplyConstraintAsync(constraint);
            }
        }

        private async Task UpdateAnimationsAsync(TimeSpan deltaTime)
        {
            // Update element animations;
            foreach (var element in _elements.Values.Where(e => e.Animation != null))
            {
                element.Animation.Update(deltaTime);

                if (element.Animation.IsComplete)
                {
                    // Apply final animation state;
                    await UpdateElementAsync(element.Id, new ElementUpdate;
                    {
                        Position = element.Animation.FinalPosition ?? element.Position,
                        Size = element.Animation.FinalSize ?? element.Size,
                        Opacity = element.Animation.FinalOpacity ?? element.Opacity;
                    });

                    element.Animation = null;
                }
            }

            await Task.CompletedTask;
        }

        private void UpdateStatistics(TimeSpan deltaTime)
        {
            _currentStatistics.UpdateTime += deltaTime;
            _currentStatistics.FrameCount++;

            // Calculate elements per frame;
            if (_currentStatistics.UpdateTime.TotalSeconds >= 1.0)
            {
                _currentStatistics.ElementsPerSecond = _currentStatistics.ElementsRendered / (float)_currentStatistics.UpdateTime.TotalSeconds;

                // Reset for next second;
                _currentStatistics.ElementsRendered = 0;
                _currentStatistics.UpdateTime = TimeSpan.Zero;
                _currentStatistics.FrameCount = 0;
            }
        }

        private void UpdateMetrics()
        {
            _metrics.TotalLayouts = _layouts.Count;
            _metrics.TotalElements = _elements.Count;
            _metrics.TotalConstraints = _constraints.Count;
            _metrics.VisibleElements = _elements.Count(e => e.Value.IsVisible);
            _metrics.ActiveConstraints = _constraints.Count(c => c.Value.IsActive);
            _metrics.LastUpdate = DateTime.UtcNow;
        }

        #endregion;

        #region Validation Methods;

        private void ValidateConfiguration(LayoutConfiguration configuration)
        {
            if (configuration == null)
                throw new ArgumentNullException(nameof(configuration));
        }

        private void ValidateLayoutName(string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
                throw new ArgumentException("Layout name cannot be null or empty", nameof(layoutName));
        }

        private void ValidateLayoutConfiguration(LayoutConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
        }

        private void ValidateElementId(string elementId)
        {
            if (string.IsNullOrWhiteSpace(elementId))
                throw new ArgumentException("Element ID cannot be null or empty", nameof(elementId));
        }

        private void ValidateElementConfiguration(ElementConfiguration config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            if (config.Size.Width <= 0 || config.Size.Height <= 0)
                throw new ArgumentException("Element size must be positive", nameof(config.Size));
        }

        private void ValidateElementUpdate(ElementUpdate update)
        {
            if (update == null)
                throw new ArgumentNullException(nameof(update));
        }

        private void ValidateConstraint(LayoutConstraint constraint)
        {
            if (constraint == null)
                throw new ArgumentNullException(nameof(constraint));

            if (string.IsNullOrWhiteSpace(constraint.Id))
                throw new ArgumentException("Constraint ID cannot be null or empty", nameof(constraint.Id));
        }

        private void ValidateConstraintId(string constraintId)
        {
            if (string.IsNullOrWhiteSpace(constraintId))
                throw new ArgumentException("Constraint ID cannot be null or empty", nameof(constraintId));
        }

        private void ValidateTemplateName(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Template name cannot be null or empty", nameof(templateName));
        }

        private void ValidateScreenSize(ScreenSize screenSize)
        {
            // Screen size validation;
            if (screenSize.Width <= 0 || screenSize.Height <= 0)
                throw new ArgumentException("Screen size width and height must be positive");
        }

        #endregion;

        #region Event Methods;

        private void InitializeEventHandlers()
        {
            LayoutStateChanged += OnLayoutStateChangedInternal;
            ScreenSizeChanged += OnScreenSizeChangedInternal;
            OrientationChanged += OnOrientationChangedInternal;
            ActiveLayoutChanged += OnActiveLayoutChangedInternal;
            ElementAdded += OnElementAddedInternal;
            ElementRemoved += OnElementRemovedInternal;
            ElementUpdated += OnElementUpdatedInternal;
            ConstraintAdded += OnConstraintAddedInternal;
            ConstraintRemoved += OnConstraintRemovedInternal;
            TemplateCreated += OnTemplateCreatedInternal;
            TemplateApplied += OnTemplateAppliedInternal;
            LayoutError += OnLayoutErrorInternal;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected virtual void OnLayoutStateChanged(LayoutStateEventArgs e)
        {
            LayoutStateChanged?.Invoke(this, e);
        }

        protected virtual void OnScreenSizeChanged(ScreenSizeEventArgs e)
        {
            ScreenSizeChanged?.Invoke(this, e);
        }

        protected virtual void OnOrientationChanged(OrientationEventArgs e)
        {
            OrientationChanged?.Invoke(this, e);
        }

        protected virtual void OnActiveLayoutChanged(LayoutEventArgs e)
        {
            ActiveLayoutChanged?.Invoke(this, e);
        }

        protected virtual void OnElementAdded(ElementEventArgs e)
        {
            ElementAdded?.Invoke(this, e);
        }

        protected virtual void OnElementRemoved(ElementEventArgs e)
        {
            ElementRemoved?.Invoke(this, e);
        }

        protected virtual void OnElementUpdated(ElementEventArgs e)
        {
            ElementUpdated?.Invoke(this, e);
        }

        protected virtual void OnConstraintAdded(ConstraintEventArgs e)
        {
            ConstraintAdded?.Invoke(this, e);
        }

        protected virtual void OnConstraintRemoved(ConstraintEventArgs e)
        {
            ConstraintRemoved?.Invoke(this, e);
        }

        protected virtual void OnTemplateCreated(TemplateEventArgs e)
        {
            TemplateCreated?.Invoke(this, e);
        }

        protected virtual void OnTemplateApplied(TemplateEventArgs e)
        {
            TemplateApplied?.Invoke(this, e);
        }

        protected virtual void OnLayoutError(LayoutErrorEventArgs e)
        {
            LayoutError?.Invoke(this, e);
        }

        private void OnLayoutStateChangedInternal(object sender, LayoutStateEventArgs e)
        {
            _logger.LogInformation($"Layout state changed: {e.PreviousState} -> {e.NewState}");
        }

        private void OnScreenSizeChangedInternal(object sender, ScreenSizeEventArgs e)
        {
            _logger.LogInformation($"Screen size changed: {e.ScreenSize}");
        }

        private void OnOrientationChangedInternal(object sender, OrientationEventArgs e)
        {
            _logger.LogInformation($"Orientation changed: {e.Orientation}");
        }

        private void OnActiveLayoutChangedInternal(object sender, LayoutEventArgs e)
        {
            _logger.LogInformation($"Active layout changed: {e.Layout?.Name ?? "None"}");
        }

        private void OnElementAddedInternal(object sender, ElementEventArgs e)
        {
            _logger.LogDebug($"Element added: {e.Element.Id}");
        }

        private void OnElementRemovedInternal(object sender, ElementEventArgs e)
        {
            _logger.LogDebug($"Element removed: {e.Element.Id}");
        }

        private void OnElementUpdatedInternal(object sender, ElementEventArgs e)
        {
            _logger.LogTrace($"Element updated: {e.Element.Id}");
        }

        private void OnConstraintAddedInternal(object sender, ConstraintEventArgs e)
        {
            _logger.LogDebug($"Constraint added: {e.Constraint.Id}");
        }

        private void OnConstraintRemovedInternal(object sender, ConstraintEventArgs e)
        {
            _logger.LogDebug($"Constraint removed: {e.Constraint.Id}");
        }

        private void OnTemplateCreatedInternal(object sender, TemplateEventArgs e)
        {
            _logger.LogInformation($"Template created: {e.Template.Name}");
        }

        private void OnTemplateAppliedInternal(object sender, TemplateEventArgs e)
        {
            _logger.LogInformation($"Template applied: {e.Template.Name}");
        }

        private void OnLayoutErrorInternal(object sender, LayoutErrorEventArgs e)
        {
            _logger.LogError(e.Exception, $"Layout error: {e.ErrorMessage}");
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
                    _layouts.Clear();
                    _elements.Clear();
                    _constraints.Clear();
                    _templates.Clear();
                    _commandQueue.Clear();

                    // Unsubscribe events;
                    LayoutStateChanged -= OnLayoutStateChangedInternal;
                    ScreenSizeChanged -= OnScreenSizeChangedInternal;
                    OrientationChanged -= OnOrientationChangedInternal;
                    ActiveLayoutChanged -= OnActiveLayoutChangedInternal;
                    ElementAdded -= OnElementAddedInternal;
                    ElementRemoved -= OnElementRemovedInternal;
                    ElementUpdated -= OnElementUpdatedInternal;
                    ConstraintAdded -= OnConstraintAddedInternal;
                    ConstraintRemoved -= OnConstraintRemovedInternal;
                    TemplateCreated -= OnTemplateCreatedInternal;
                    TemplateApplied -= OnTemplateAppliedInternal;
                    LayoutError -= OnLayoutErrorInternal;
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
    /// Layout state;
    /// </summary>
    public enum LayoutState;
    {
        Uninitialized,
        Initializing,
        Ready,
        Active,
        Updating,
        Error,
        ShuttingDown;
    }

    /// <summary>
    /// Layout types;
    /// </summary>
    public enum LayoutType;
    {
        Standard,
        Responsive,
        Adaptive,
        Fluid,
        Fixed,
        Hybrid;
    }

    /// <summary>
    /// Element types;
    /// </summary>
    public enum ElementType;
    {
        Button,
        Panel,
        Text,
        Image,
        Input,
        Container,
        Grid,
        List,
        Menu,
        Custom;
    }

    /// <summary>
    /// Screen sizes;
    /// </summary>
    public enum ScreenSize;
    {
        Mobile,
        Tablet,
        Desktop,
        Wide,
        Custom;
    }

    /// <summary>
    /// Screen orientation;
    /// </summary>
    public enum Orientation;
    {
        Portrait,
        Landscape;
    }

    /// <summary>
    /// Constraint types;
    /// </summary>
    public enum ConstraintType;
    {
        Alignment,
        Spacing,
        Size,
        Position,
        Proportional,
        AspectRatio,
        Margin,
        Padding;
    }

    /// <summary>
    /// Alignment options;
    /// </summary>
    public enum Alignment;
    {
        Top,
        Bottom,
        Left,
        Right,
        CenterHorizontal,
        CenterVertical,
        Center;
    }

    /// <summary>
    /// Anchor points;
    /// </summary>
    public enum Anchor;
    {
        TopLeft,
        TopCenter,
        TopRight,
        MiddleLeft,
        MiddleCenter,
        MiddleRight,
        BottomLeft,
        BottomCenter,
        BottomRight,
        Stretch;
    }

    /// <summary>
    /// Directions for spacing;
    /// </summary>
    public enum Direction;
    {
        Up,
        Down,
        Left,
        Right;
    }

    /// <summary>
    /// Dimensions;
    /// </summary>
    public enum Dimension;
    {
        Width,
        Height,
        Both;
    }

    /// <summary>
    /// Axes;
    /// </summary>
    public enum Axis;
    {
        X,
        Y;
    }

    /// <summary>
    /// Constraint enforcement levels;
    /// </summary>
    public enum ConstraintEnforcement;
    {
        None,
        Warning,
        Strict;
    }

    /// <summary>
    /// Template types;
    /// </summary>
    public enum TemplateType;
    {
        Grid,
        Sidebar,
        HeaderFooter,
        Card,
        Modal,
        FromCurrent,
        Custom;
    }

    /// <summary>
    /// Collision severity levels;
    /// </summary>
    public enum CollisionSeverity;
    {
        None,
        Low,
        Medium,
        High,
        Critical;
    }

    /// <summary>
    /// UI Layout;
    /// </summary>
    public class UILayout;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public LayoutType Type { get; set; }
        public LayoutConfiguration Configuration { get; set; }
        public ScreenSize ScreenSize { get; set; }
        public Orientation Orientation { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public DateTime? LastApplied { get; set; }
        public DateTime? LastDeactivated { get; set; }
        public Dictionary<string, UIElement> Elements { get; set; }
        public Dictionary<string, LayoutConstraint> Constraints { get; set; }
    }

    /// <summary>
    /// UI Element;
    /// </summary>
    public class UIElement;
    {
        public string Id { get; set; }
        public ElementType Type { get; set; }
        public ElementConfiguration Configuration { get; set; }
        public Point Position { get; set; }
        public Size Size { get; set; }
        public Anchor Anchor { get; set; }
        public Thickness Margin { get; set; }
        public Thickness Padding { get; set; }
        public bool IsVisible { get; set; }
        public int ZIndex { get; set; }
        public float Opacity { get; set; } = 1.0f;
        public ElementAnimation Animation { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastUpdated { get; set; }
        public DateTime LastRendered { get; set; }

        public UIElement Clone()
        {
            return new UIElement;
            {
                Id = Guid.NewGuid().ToString(),
                Type = this.Type,
                Configuration = this.Configuration?.Clone(),
                Position = this.Position,
                Size = this.Size,
                Anchor = this.Anchor,
                Margin = this.Margin,
                Padding = this.Padding,
                IsVisible = this.IsVisible,
                ZIndex = this.ZIndex,
                Opacity = this.Opacity,
                CreatedTime = DateTime.UtcNow,
                LastUpdated = this.LastUpdated;
            };
        }
    }

    /// <summary>
    /// Element configuration;
    /// </summary>
    public class ElementConfiguration;
    {
        public Point Position { get; set; }
        public Size Size { get; set; }
        public Anchor Anchor { get; set; } = Anchor.TopLeft;
        public Thickness Margin { get; set; } = new Thickness(0);
        public Thickness Padding { get; set; } = new Thickness(0);
        public bool IsVisible { get; set; } = true;
        public int ZIndex { get; set; } = 0;
        public string Layer { get; set; } = "Default";
        public ResponsiveSettings ResponsiveSettings { get; set; }
        public bool CreateDisplayWidget { get; set; } = true;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();

        public ElementConfiguration Clone()
        {
            return new ElementConfiguration;
            {
                Position = this.Position,
                Size = this.Size,
                Anchor = this.Anchor,
                Margin = this.Margin,
                Padding = this.Padding,
                IsVisible = this.IsVisible,
                ZIndex = this.ZIndex,
                Layer = this.Layer,
                ResponsiveSettings = this.ResponsiveSettings?.Clone(),
                CreateDisplayWidget = this.CreateDisplayWidget,
                Properties = new Dictionary<string, object>(this.Properties)
            };
        }
    }

    /// <summary>
    /// Element update parameters;
    /// </summary>
    public class ElementUpdate;
    {
        public Point? Position { get; set; }
        public Size? Size { get; set; }
        public Anchor? Anchor { get; set; }
        public Thickness? Margin { get; set; }
        public Thickness? Padding { get; set; }
        public bool? IsVisible { get; set; }
        public int? ZIndex { get; set; }
        public float? Opacity { get; set; }
        public Dictionary<string, object> Properties { get; set; }
    }

    /// <summary>
    /// Responsive settings for elements;
    /// </summary>
    public class ResponsiveSettings;
    {
        public Size? MobileSize { get; set; }
        public Size? TabletSize { get; set; }
        public Size? DesktopSize { get; set; }
        public Size? WideSize { get; set; }
        public Point? PortraitPosition { get; set; }
        public Point? LandscapePosition { get; set; }
        public Dictionary<ScreenSize, Size> SizeOverrides { get; set; } = new Dictionary<ScreenSize, Size>();
        public Dictionary<Orientation, Point> PositionOverrides { get; set; } = new Dictionary<Orientation, Point>();

        public ResponsiveSettings Clone()
        {
            return new ResponsiveSettings;
            {
                MobileSize = this.MobileSize,
                TabletSize = this.TabletSize,
                DesktopSize = this.DesktopSize,
                WideSize = this.WideSize,
                PortraitPosition = this.PortraitPosition,
                LandscapePosition = this.LandscapePosition,
                SizeOverrides = new Dictionary<ScreenSize, Size>(this.SizeOverrides),
                PositionOverrides = new Dictionary<Orientation, Point>(this.PositionOverrides)
            };
        }
    }

    /// <summary>
    /// Layout constraint;
    /// </summary>
    public class LayoutConstraint;
    {
        public string Id { get; set; }
        public ConstraintType Type { get; set; }
        public string SourceElementId { get; set; }
        public string TargetElementId { get; set; }
        public float Value { get; set; }
        public Alignment Alignment { get; set; }
        public ConstraintEnforcement Enforcement { get; set; } = ConstraintEnforcement.Strict;
        public bool IsActive { get; set; } = true;
        public DateTime CreatedTime { get; set; }
        public DateTime LastApplied { get; set; }
        public TimeSpan CheckInterval { get; set; } = TimeSpan.FromSeconds(1);
        public TimeSpan UpdateTime { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public LayoutConstraint Clone()
        {
            return new LayoutConstraint;
            {
                Id = Guid.NewGuid().ToString(),
                Type = this.Type,
                SourceElementId = this.SourceElementId,
                TargetElementId = this.TargetElementId,
                Value = this.Value,
                Alignment = this.Alignment,
                Enforcement = this.Enforcement,
                IsActive = this.IsActive,
                CreatedTime = DateTime.UtcNow,
                LastApplied = this.LastApplied,
                CheckInterval = this.CheckInterval,
                UpdateTime = this.UpdateTime,
                Parameters = new Dictionary<string, object>(this.Parameters)
            };
        }
    }

    /// <summary>
    /// Layout template;
    /// </summary>
    public class LayoutTemplate;
    {
        public string Name { get; set; }
        public TemplateType Type { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public Dictionary<string, UIElement> Elements { get; set; }
        public Dictionary<string, LayoutConstraint> Constraints { get; set; }
        public List<ScreenSize> ScreenSizes { get; set; }
        public List<Orientation> SupportedOrientations { get; set; } = new List<Orientation> { Orientation.Landscape, Orientation.Portrait };
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Layout configuration;
    /// </summary>
    public class LayoutConfiguration;
    {
        public ScreenSize DefaultScreenSize { get; set; } = ScreenSize.Desktop;
        public Orientation DefaultOrientation { get; set; } = Orientation.Landscape;
        public bool EnableResponsiveDesign { get; set; } = true;
        public bool EnableAutoLayout { get; set; } = true;
        public float DefaultSpacing { get; set; } = 10.0f;
        public Thickness DefaultMargin { get; set; } = new Thickness(5);
        public Thickness DefaultPadding { get; set; } = new Thickness(5);
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Layout metrics;
    /// </summary>
    public class LayoutMetrics;
    {
        public int TotalLayouts { get; set; }
        public int TotalElements { get; set; }
        public int TotalConstraints { get; set; }
        public int VisibleElements { get; set; }
        public int ActiveConstraints { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Layout statistics;
    /// </summary>
    public class LayoutStatistics;
    {
        public long ElementsRendered { get; set; }
        public long FramesRendered { get; set; }
        public float ElementsPerSecond { get; set; }
        public TimeSpan UpdateTime { get; set; }
        public long FrameCount { get; set; }
    }

    /// <summary>
    /// Layout manager statistics;
    /// </summary>
    public class LayoutManagerStatistics;
    {
        public int TotalLayouts { get; set; }
        public int TotalElements { get; set; }
        public int TotalConstraints { get; set; }
        public int TotalTemplates { get; set; }
        public TimeSpan Uptime { get; set; }
        public LayoutState State { get; set; }
        public ScreenSize ScreenSize { get; set; }
        public Orientation Orientation { get; set; }
        public LayoutMetrics Metrics { get; set; }
        public LayoutStatistics LayoutStatistics { get; set; }
        public DateTime LastUpdate { get; set; }
    }

    /// <summary>
    /// Element animation;
    /// </summary>
    public class ElementAnimation;
    {
        public AnimationType Type { get; set; }
        public TimeSpan Duration { get; set; }
        public Point? StartPosition { get; set; }
        public Point? FinalPosition { get; set; }
        public Size? StartSize { get; set; }
        public Size? FinalSize { get; set; }
        public float? StartOpacity { get; set; }
        public float? FinalOpacity { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public bool IsComplete => ElapsedTime >= Duration;

        public void Update(TimeSpan deltaTime)
        {
            ElapsedTime += deltaTime;
        }
    }

    /// <summary>
    /// Animation types;
    /// </summary>
    public enum AnimationType;
    {
        Move,
        Resize,
        Fade,
        Slide,
        Scale,
        Rotate,
        Custom;
    }

    /// <summary>
    /// Element collision;
    /// </summary>
    public class ElementCollision;
    {
        public UIElement Element1 { get; set; }
        public UIElement Element2 { get; set; }
        public Rectangle Intersection { get; set; }
        public CollisionSeverity Severity { get; set; }
    }

    /// <summary>
    /// Layout command;
    /// </summary>
    public class LayoutCommand;
    {
        public LayoutCommandType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedTime { get; set; }
    }

    /// <summary>
    /// Layout command types;
    /// </summary>
    public enum LayoutCommandType;
    {
        CreateLayout,
        DeleteLayout,
        ApplyLayout,
        AddElement,
        RemoveElement,
        UpdateElement,
        AddConstraint,
        RemoveConstraint,
        CreateTemplate,
        ApplyTemplate,
        UpdateScreenSize,
        ClearLayout;
    }

    #region Geometry Structures;

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

        public float Area => Width * Height;

        public static Size Zero => new Size(0, 0);
    }

    /// <summary>
    /// Rectangle structure;
    /// </summary>
    public struct Rectangle;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }

        public Rectangle(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public float Area => Width * Height;

        public bool Intersects(Rectangle other)
        {
            return X < other.X + other.Width &&
                   X + Width > other.X &&
                   Y < other.Y + other.Height &&
                   Y + Height > other.Y;
        }

        public Rectangle Intersection(Rectangle other)
        {
            if (!Intersects(other))
                return new Rectangle(0, 0, 0, 0);

            float x1 = Math.Max(X, other.X);
            float x2 = Math.Min(X + Width, other.X + other.Width);
            float y1 = Math.Max(Y, other.Y);
            float y2 = Math.Min(Y + Height, other.Y + other.Height);

            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }
    }

    /// <summary>
    /// Thickness structure for margins and padding;
    /// </summary>
    public struct Thickness;
    {
        public float Left { get; set; }
        public float Top { get; set; }
        public float Right { get; set; }
        public float Bottom { get; set; }

        public Thickness(float uniformLength)
        {
            Left = Top = Right = Bottom = uniformLength;
        }

        public Thickness(float left, float top, float right, float bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    /// <summary>
    /// Screen size structure;
    /// </summary>
    public struct ScreenSize;
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Name { get; set; }

        public static ScreenSize Default => new ScreenSize;
        {
            Width = 1920,
            Height = 1080,
            Name = "FullHD"
        };

        public static ScreenSize Mobile => new ScreenSize;
        {
            Width = 375,
            Height = 667,
            Name = "Mobile"
        };

        public static ScreenSize Tablet => new ScreenSize;
        {
            Width = 768,
            Height = 1024,
            Name = "Tablet"
        };

        public static ScreenSize Desktop => new ScreenSize;
        {
            Width = 1366,
            Height = 768,
            Name = "Desktop"
        };

        public static ScreenSize Wide => new ScreenSize;
        {
            Width = 2560,
            Height = 1440,
            Name = "Wide"
        };

        public override string ToString() => $"{Name} ({Width}x{Height})";
    }

    #endregion;

    #region Event Arguments;

    /// <summary>
    /// Layout state event arguments;
    /// </summary>
    public class LayoutStateEventArgs : EventArgs;
    {
        public LayoutState PreviousState { get; set; }
        public LayoutState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Screen size event arguments;
    /// </summary>
    public class ScreenSizeEventArgs : EventArgs;
    {
        public ScreenSize ScreenSize { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Orientation event arguments;
    /// </summary>
    public class OrientationEventArgs : EventArgs;
    {
        public Orientation Orientation { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Layout event arguments;
    /// </summary>
    public class LayoutEventArgs : EventArgs;
    {
        public UILayout Layout { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Element event arguments;
    /// </summary>
    public class ElementEventArgs : EventArgs;
    {
        public UIElement Element { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Constraint event arguments;
    /// </summary>
    public class ConstraintEventArgs : EventArgs;
    {
        public LayoutConstraint Constraint { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Template event arguments;
    /// </summary>
    public class TemplateEventArgs : EventArgs;
    {
        public LayoutTemplate Template { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Layout error event arguments;
    /// </summary>
    public class LayoutErrorEventArgs : EventArgs;
    {
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Custom exception for layout manager errors;
    /// </summary>
    public class LayoutManagerException : Exception
    {
        public LayoutManagerException() { }
        public LayoutManagerException(string message) : base(message) { }
        public LayoutManagerException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #endregion;
}
