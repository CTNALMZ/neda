using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Security;
using NEDA.Interface.VisualInterface.MainDashboard.Contracts;
using NEDA.Services.Messaging.EventBus;
using NEDA.Monitoring.PerformanceCounters;
using NEDA.Monitoring.HealthChecks;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace NEDA.Interface.VisualInterface.MainDashboard;
{
    /// <summary>
    /// Ana kontrol paneli ve dashboard yönetim sistemi;
    /// </summary>
    public class Dashboard : INotifyPropertyChanged, IDashboard;
    {
        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IPermissionValidator _permissionValidator;
        private readonly IEventBus _eventBus;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IHealthChecker _healthChecker;
        private readonly DashboardConfiguration _configuration;

        private readonly ConcurrentDictionary<string, DashboardWidget> _widgets;
        private readonly ConcurrentDictionary<string, DashboardPanel> _panels;
        private readonly SemaphoreSlim _refreshLock = new SemaphoreSlim(1, 1);
        private readonly System.Timers.Timer _autoRefreshTimer;
        private readonly System.Timers.Timer _metricsUpdateTimer;
        private DateTime _lastFullRefresh = DateTime.MinValue;

        private string _currentLayoutId;
        private DashboardTheme _currentTheme;
        private DashboardState _state;
        private bool _isInitialized;
        private CancellationTokenSource _dashboardCts;

        /// <summary>
        /// Dashboard sınıfı için constructor;
        /// </summary>
        public Dashboard(
            ILogger logger,
            IErrorHandler errorHandler,
            IPermissionValidator permissionValidator,
            IEventBus eventBus,
            IPerformanceMonitor performanceMonitor,
            IHealthChecker healthChecker,
            DashboardConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _permissionValidator = permissionValidator ?? throw new ArgumentNullException(nameof(permissionValidator));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
            _configuration = configuration ?? DashboardConfiguration.Default;

            _widgets = new ConcurrentDictionary<string, DashboardWidget>();
            _panels = new ConcurrentDictionary<string, DashboardPanel>();
            _state = DashboardState.Stopped;
            _currentTheme = DashboardTheme.Default;

            // Otomatik yenileme timer'ı;
            _autoRefreshTimer = new System.Timers.Timer(_configuration.AutoRefreshInterval.TotalMilliseconds);
            _autoRefreshTimer.Elapsed += async (s, e) => await AutoRefreshAsync();
            _autoRefreshTimer.AutoReset = true;

            // Metrik güncelleme timer'ı;
            _metricsUpdateTimer = new System.Timers.Timer(_configuration.MetricsUpdateInterval.TotalMilliseconds);
            _metricsUpdateTimer.Elapsed += async (s, e) => await UpdateMetricsAsync();
            _metricsUpdateTimer.AutoReset = true;

            // Olay abonelikleri;
            SubscribeToEvents();

            PropertyChanged += OnDashboardPropertyChanged;
        }

        #region Public Properties;

        /// <summary>
        /// Dashboard başlığı;
        /// </summary>
        public string Title;
        {
            get => _title;
            set => SetField(ref _title, value);
        }
        private string _title = "NEDA Dashboard";

        /// <summary>
        /// Dashboard açıklaması;
        /// </summary>
        public string Description;
        {
            get => _description;
            set => SetField(ref _description, value);
        }
        private string _description = "Sistem kontrol paneli ve izleme dashboard'ı";

        /// <summary>
        /// Dashboard versiyonu;
        /// </summary>
        public string Version;
        {
            get => _version;
            set => SetField(ref _version, value);
        }
        private string _version = "1.0.0";

        /// <summary>
        /// Dashboard durumu;
        /// </summary>
        public DashboardState State;
        {
            get => _state;
            private set => SetField(ref _state, value);
        }

        /// <summary>
        /// Mevcut tema;
        /// </summary>
        public DashboardTheme CurrentTheme;
        {
            get => _currentTheme;
            set => SetField(ref _currentTheme, value);
        }

        /// <summary>
        /// Mevcut layout ID;
        /// </summary>
        public string CurrentLayoutId;
        {
            get => _currentLayoutId;
            set => SetField(ref _currentLayoutId, value);
        }

        /// <summary>
        /// Dashboard widget'ları;
        /// </summary>
        public ObservableCollection<DashboardWidget> Widgets { get; } = new ObservableCollection<DashboardWidget>();

        /// <summary>
        /// Dashboard panelleri;
        /// </summary>
        public ObservableCollection<DashboardPanel> Panels { get; } = new ObservableCollection<DashboardPanel>();

        /// <summary>
        /// Sistem metrikleri;
        /// </summary>
        public DashboardMetrics Metrics;
        {
            get => _metrics;
            private set => SetField(ref _metrics, value);
        }
        private DashboardMetrics _metrics = new DashboardMetrics();

        /// <summary>
        /// Sistem sağlık durumu;
        /// </summary>
        public SystemHealthStatus HealthStatus;
        {
            get => _healthStatus;
            private set => SetField(ref _healthStatus, value);
        }
        private SystemHealthStatus _healthStatus = new SystemHealthStatus();

        /// <summary>
        /// Son yenileme zamanı;
        /// </summary>
        public DateTime LastRefreshTime;
        {
            get => _lastRefreshTime;
            private set => SetField(ref _lastRefreshTime, value);
        }
        private DateTime _lastRefreshTime;

        /// <summary>
        /// Dashboard'ın başlatılıp başlatılmadığı;
        /// </summary>
        public bool IsInitialized;
        {
            get => _isInitialized;
            private set => SetField(ref _isInitialized, value);
        }

        /// <summary>
        /// Dashboard kullanıcı ayarları;
        /// </summary>
        public DashboardUserSettings UserSettings;
        {
            get => _userSettings;
            set => SetField(ref _userSettings, value);
        }
        private DashboardUserSettings _userSettings = new DashboardUserSettings();

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Dashboard'ı başlatır;
        /// </summary>
        public async Task InitializeAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (State != DashboardState.Stopped)
                throw new InvalidOperationException($"Dashboard is already in {State} state");

            try
            {
                State = DashboardState.Initializing;
                _dashboardCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                _logger.LogInformation("Initializing dashboard...");

                // Yetki kontrolü;
                if (!await _permissionValidator.HasPermissionAsync(userId, "Dashboard.Access"))
                {
                    throw new UnauthorizedAccessException($"User '{userId}' does not have permission to access dashboard");
                }

                // Varsayılan widget'ları yükle;
                await LoadDefaultWidgetsAsync(_dashboardCts.Token);

                // Varsayılan panelleri yükle;
                await LoadDefaultPanelsAsync(_dashboardCts.Token);

                // Sistem sağlık durumunu kontrol et;
                await RefreshHealthStatusAsync(_dashboardCts.Token);

                // Performans metriklerini yükle;
                await RefreshMetricsAsync(_dashboardCts.Token);

                // Kullanıcı ayarlarını yükle;
                await LoadUserSettingsAsync(userId, _dashboardCts.Token);

                // Timer'ları başlat;
                StartTimers();

                IsInitialized = true;
                State = DashboardState.Running;
                LastRefreshTime = DateTime.UtcNow;

                await _eventBus.PublishAsync(new DashboardInitializedEvent;
                {
                    DashboardId = GetDashboardId(),
                    UserId = userId,
                    Timestamp = DateTime.UtcNow,
                    WidgetCount = Widgets.Count,
                    PanelCount = Panels.Count;
                }, _dashboardCts.Token);

                _logger.LogInformation($"Dashboard initialized successfully. Widgets: {Widgets.Count}, Panels: {Panels.Count}");
            }
            catch (Exception ex)
            {
                State = DashboardState.Error;
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "Initialize",
                    UserId = userId,
                    AdditionalData = new { DashboardState = State }
                });
                throw;
            }
        }

        /// <summary>
        /// Dashboard'ı durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (State == DashboardState.Stopped)
                return;

            try
            {
                State = DashboardState.ShuttingDown;

                _logger.LogInformation("Shutting down dashboard...");

                // Timer'ları durdur;
                StopTimers();

                // Widget'ları temizle;
                await CleanupWidgetsAsync();

                // Panelleri temizle;
                await CleanupPanelsAsync();

                // İptal token'ını iptal et;
                _dashboardCts?.Cancel();
                _dashboardCts?.Dispose();

                // Olay aboneliklerini temizle;
                UnsubscribeFromEvents();

                State = DashboardState.Stopped;
                IsInitialized = false;

                await _eventBus.PublishAsync(new DashboardShutdownEvent;
                {
                    DashboardId = GetDashboardId(),
                    Timestamp = DateTime.UtcNow;
                }, CancellationToken.None);

                _logger.LogInformation("Dashboard shutdown completed");
            }
            catch (Exception ex)
            {
                State = DashboardState.Error;
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "Shutdown",
                    AdditionalData = new { DashboardState = State }
                });
                throw;
            }
        }

        /// <summary>
        /// Dashboard'ı yeniler;
        /// </summary>
        public async Task<RefreshResult> RefreshAsync(bool forceFullRefresh = false, CancellationToken cancellationToken = default)
        {
            if (State != DashboardState.Running)
                throw new InvalidOperationException($"Cannot refresh dashboard in {State} state");

            await _refreshLock.WaitAsync(cancellationToken);

            var refreshId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;
            var refreshStats = new RefreshStatistics();

            try
            {
                _logger.LogInformation($"Starting dashboard refresh (ID: {refreshId})");

                // Yenileme başlangıç olayı;
                await _eventBus.PublishAsync(new DashboardRefreshStartedEvent;
                {
                    RefreshId = refreshId,
                    DashboardId = GetDashboardId(),
                    IsFullRefresh = forceFullRefresh,
                    Timestamp = startTime;
                }, cancellationToken);

                // Sistem sağlık durumunu yenile;
                refreshStats.HealthCheckTime = await MeasureExecutionTimeAsync(
                    () => RefreshHealthStatusAsync(cancellationToken));

                // Metrikleri yenile;
                refreshStats.MetricsUpdateTime = await MeasureExecutionTimeAsync(
                    () => RefreshMetricsAsync(cancellationToken));

                // Widget'ları yenile;
                refreshStats.WidgetRefreshTime = await MeasureExecutionTimeAsync(
                    () => RefreshWidgetsAsync(forceFullRefresh, cancellationToken));

                // Panelleri yenile;
                refreshStats.PanelRefreshTime = await MeasureExecutionTimeAsync(
                    () => RefreshPanelsAsync(cancellationToken));

                LastRefreshTime = DateTime.UtcNow;
                _lastFullRefresh = forceFullRefresh ? DateTime.UtcNow : _lastFullRefresh;

                var totalTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                refreshStats.TotalTime = totalTime;

                var result = new RefreshResult;
                {
                    RefreshId = refreshId,
                    IsSuccess = true,
                    RefreshTime = totalTime,
                    Statistics = refreshStats,
                    Timestamp = DateTime.UtcNow;
                };

                // Yenileme tamamlandı olayı;
                await _eventBus.PublishAsync(new DashboardRefreshCompletedEvent;
                {
                    RefreshId = refreshId,
                    DashboardId = GetDashboardId(),
                    IsSuccess = true,
                    RefreshTime = totalTime,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Dashboard refresh completed (ID: {refreshId}, Time: {totalTime:F2}ms)");

                return result;
            }
            catch (Exception ex)
            {
                var totalTime = (DateTime.UtcNow - startTime).TotalMilliseconds;

                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "Refresh",
                    AdditionalData = new { RefreshId = refreshId, ForceFullRefresh = forceFullRefresh }
                });

                // Yenileme başarısız olayı;
                await _eventBus.PublishAsync(new DashboardRefreshFailedEvent;
                {
                    RefreshId = refreshId,
                    DashboardId = GetDashboardId(),
                    Error = ex.Message,
                    RefreshTime = totalTime,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogError($"Dashboard refresh failed (ID: {refreshId}): {ex.Message}");

                return new RefreshResult;
                {
                    RefreshId = refreshId,
                    IsSuccess = false,
                    Error = ex.Message,
                    RefreshTime = totalTime,
                    Timestamp = DateTime.UtcNow;
                };
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// Widget ekler;
        /// </summary>
        public async Task<WidgetOperationResult> AddWidgetAsync(
            DashboardWidget widget,
            string panelId = null,
            CancellationToken cancellationToken = default)
        {
            if (widget == null)
                throw new ArgumentNullException(nameof(widget));

            if (string.IsNullOrWhiteSpace(widget.Id))
                widget.Id = Guid.NewGuid().ToString();

            try
            {
                // Widget doğrulama;
                var validation = ValidateWidget(widget);
                if (!validation.IsValid)
                {
                    return WidgetOperationResult.Failure(
                        $"Widget validation failed: {string.Join(", ", validation.Errors)}");
                }

                // Çakışma kontrolü;
                if (_widgets.ContainsKey(widget.Id))
                {
                    return WidgetOperationResult.Failure($"Widget with id '{widget.Id}' already exists");
                }

                // Panel kontrolü;
                if (!string.IsNullOrWhiteSpace(panelId))
                {
                    if (!_panels.ContainsKey(panelId))
                    {
                        return WidgetOperationResult.Failure($"Panel with id '{panelId}' not found");
                    }

                    widget.PanelId = panelId;
                }

                // Widget'ı başlat;
                await widget.InitializeAsync(cancellationToken);

                // Widget'ı ekle;
                if (_widgets.TryAdd(widget.Id, widget))
                {
                    // ObservableCollection'a ekle (UI thread güvenli)
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        Widgets.Add(widget);
                    });

                    // Panele widget ekle;
                    if (!string.IsNullOrWhiteSpace(panelId) && _panels.TryGetValue(panelId, out var panel))
                    {
                        await ExecuteOnUiThreadAsync(() =>
                        {
                            panel.Widgets.Add(widget);
                        });
                    }

                    await _eventBus.PublishAsync(new WidgetAddedEvent;
                    {
                        WidgetId = widget.Id,
                        WidgetType = widget.Type,
                        PanelId = panelId,
                        DashboardId = GetDashboardId(),
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Widget added: {widget.Title} (ID: {widget.Id}, Type: {widget.Type})");

                    return WidgetOperationResult.Success(widget.Id);
                }

                return WidgetOperationResult.Failure("Failed to add widget");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "AddWidget",
                    AdditionalData = new { WidgetId = widget.Id, WidgetType = widget.Type }
                });

                return WidgetOperationResult.Failure($"Failed to add widget: {ex.Message}");
            }
        }

        /// <summary>
        /// Widget'ı kaldırır;
        /// </summary>
        public async Task<WidgetOperationResult> RemoveWidgetAsync(string widgetId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(widgetId))
                throw new ArgumentException("Widget ID cannot be empty", nameof(widgetId));

            try
            {
                if (!_widgets.TryGetValue(widgetId, out var widget))
                {
                    return WidgetOperationResult.Failure($"Widget with id '{widgetId}' not found");
                }

                // Widget'ı temizle;
                await widget.CleanupAsync();

                // Widget'ı kaldır;
                if (_widgets.TryRemove(widgetId, out _))
                {
                    // ObservableCollection'dan kaldır;
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        Widgets.Remove(widget);
                    });

                    // Panelden kaldır;
                    if (!string.IsNullOrWhiteSpace(widget.PanelId) &&
                        _panels.TryGetValue(widget.PanelId, out var panel))
                    {
                        await ExecuteOnUiThreadAsync(() =>
                        {
                            panel.Widgets.Remove(widget);
                        });
                    }

                    await _eventBus.PublishAsync(new WidgetRemovedEvent;
                    {
                        WidgetId = widgetId,
                        WidgetType = widget.Type,
                        DashboardId = GetDashboardId(),
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Widget removed: {widget.Title} (ID: {widgetId})");

                    return WidgetOperationResult.Success(widgetId);
                }

                return WidgetOperationResult.Failure("Failed to remove widget");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "RemoveWidget",
                    AdditionalData = new { WidgetId = widgetId }
                });

                return WidgetOperationResult.Failure($"Failed to remove widget: {ex.Message}");
            }
        }

        /// <summary>
        /// Widget'ı günceller;
        /// </summary>
        public async Task<WidgetOperationResult> UpdateWidgetAsync(DashboardWidget updatedWidget, CancellationToken cancellationToken = default)
        {
            if (updatedWidget == null)
                throw new ArgumentNullException(nameof(updatedWidget));

            if (string.IsNullOrWhiteSpace(updatedWidget.Id))
                throw new ArgumentException("Widget ID cannot be empty", nameof(updatedWidget.Id));

            try
            {
                // Doğrulama;
                var validation = ValidateWidget(updatedWidget);
                if (!validation.IsValid)
                {
                    return WidgetOperationResult.Failure(
                        $"Widget validation failed: {string.Join(", ", validation.Errors)}");
                }

                if (!_widgets.TryGetValue(updatedWidget.Id, out var existingWidget))
                {
                    return WidgetOperationResult.Failure($"Widget with id '{updatedWidget.Id}' not found");
                }

                // Panel değişikliği kontrolü;
                if (existingWidget.PanelId != updatedWidget.PanelId)
                {
                    // Eski panelden kaldır;
                    if (!string.IsNullOrWhiteSpace(existingWidget.PanelId) &&
                        _panels.TryGetValue(existingWidget.PanelId, out var oldPanel))
                    {
                        await ExecuteOnUiThreadAsync(() =>
                        {
                            oldPanel.Widgets.Remove(existingWidget);
                        });
                    }

                    // Yeni panele ekle;
                    if (!string.IsNullOrWhiteSpace(updatedWidget.PanelId) &&
                        _panels.TryGetValue(updatedWidget.PanelId, out var newPanel))
                    {
                        updatedWidget.PanelId = updatedWidget.PanelId;
                        await ExecuteOnUiThreadAsync(() =>
                        {
                            newPanel.Widgets.Add(updatedWidget);
                        });
                    }
                }

                // Widget'ı güncelle;
                await existingWidget.CleanupAsync();
                await updatedWidget.InitializeAsync(cancellationToken);

                _widgets[updatedWidget.Id] = updatedWidget;

                // ObservableCollection'ı güncelle;
                await ExecuteOnUiThreadAsync(() =>
                {
                    var index = Widgets.IndexOf(existingWidget);
                    if (index >= 0)
                    {
                        Widgets[index] = updatedWidget;
                    }
                });

                await _eventBus.PublishAsync(new WidgetUpdatedEvent;
                {
                    WidgetId = updatedWidget.Id,
                    WidgetType = updatedWidget.Type,
                    DashboardId = GetDashboardId(),
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Widget updated: {updatedWidget.Title} (ID: {updatedWidget.Id})");

                return WidgetOperationResult.Success(updatedWidget.Id);
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "UpdateWidget",
                    AdditionalData = new { WidgetId = updatedWidget?.Id }
                });

                return WidgetOperationResult.Failure($"Failed to update widget: {ex.Message}");
            }
        }

        /// <summary>
        /// Panel ekler;
        /// </summary>
        public async Task<PanelOperationResult> AddPanelAsync(DashboardPanel panel, CancellationToken cancellationToken = default)
        {
            if (panel == null)
                throw new ArgumentNullException(nameof(panel));

            if (string.IsNullOrWhiteSpace(panel.Id))
                panel.Id = Guid.NewGuid().ToString();

            try
            {
                // Panel doğrulama;
                var validation = ValidatePanel(panel);
                if (!validation.IsValid)
                {
                    return PanelOperationResult.Failure(
                        $"Panel validation failed: {string.Join(", ", validation.Errors)}");
                }

                // Çakışma kontrolü;
                if (_panels.ContainsKey(panel.Id))
                {
                    return PanelOperationResult.Failure($"Panel with id '{panel.Id}' already exists");
                }

                // Panel'i başlat;
                await panel.InitializeAsync(cancellationToken);

                // Panel'i ekle;
                if (_panels.TryAdd(panel.Id, panel))
                {
                    await ExecuteOnUiThreadAsync(() =>
                    {
                        Panels.Add(panel);
                    });

                    await _eventBus.PublishAsync(new PanelAddedEvent;
                    {
                        PanelId = panel.Id,
                        PanelType = panel.Type,
                        DashboardId = GetDashboardId(),
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Panel added: {panel.Title} (ID: {panel.Id}, Type: {panel.Type})");

                    return PanelOperationResult.Success(panel.Id);
                }

                return PanelOperationResult.Failure("Failed to add panel");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "AddPanel",
                    AdditionalData = new { PanelId = panel.Id, PanelType = panel.Type }
                });

                return PanelOperationResult.Failure($"Failed to add panel: {ex.Message}");
            }
        }

        /// <summary>
        /// Dashboard layout'unu değiştirir;
        /// </summary>
        public async Task<LayoutOperationResult> ChangeLayoutAsync(string layoutId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(layoutId))
                throw new ArgumentException("Layout ID cannot be empty", nameof(layoutId));

            try
            {
                _logger.LogInformation($"Changing dashboard layout to: {layoutId}");

                // Layout yükleme;
                var layout = await LoadLayoutAsync(layoutId, cancellationToken);
                if (layout == null)
                {
                    return LayoutOperationResult.Failure($"Layout '{layoutId}' not found");
                }

                // Mevcut widget'ları temizle;
                await CleanupWidgetsAsync();

                // Mevcut panelleri temizle;
                await CleanupPanelsAsync();

                // Yeni panelleri ekle;
                foreach (var panelConfig in layout.Panels)
                {
                    var panel = CreatePanelFromConfig(panelConfig);
                    await AddPanelAsync(panel, cancellationToken);
                }

                // Yeni widget'ları ekle;
                foreach (var widgetConfig in layout.Widgets)
                {
                    var widget = CreateWidgetFromConfig(widgetConfig);
                    await AddWidgetAsync(widget, widgetConfig.PanelId, cancellationToken);
                }

                CurrentLayoutId = layoutId;

                await _eventBus.PublishAsync(new LayoutChangedEvent;
                {
                    LayoutId = layoutId,
                    DashboardId = GetDashboardId(),
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Dashboard layout changed to: {layoutId}");

                return LayoutOperationResult.Success(layoutId);
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "ChangeLayout",
                    AdditionalData = new { LayoutId = layoutId }
                });

                return LayoutOperationResult.Failure($"Failed to change layout: {ex.Message}");
            }
        }

        /// <summary>
        /// Dashboard temasını değiştirir;
        /// </summary>
        public async Task ChangeThemeAsync(DashboardTheme theme, CancellationToken cancellationToken = default)
        {
            if (CurrentTheme == theme)
                return;

            try
            {
                _logger.LogInformation($"Changing dashboard theme to: {theme}");

                // Tema değişikliği olayı;
                await _eventBus.PublishAsync(new ThemeChangingEvent;
                {
                    OldTheme = CurrentTheme,
                    NewTheme = theme,
                    DashboardId = GetDashboardId(),
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                // Widget'ların temasını güncelle;
                foreach (var widget in _widgets.Values)
                {
                    await widget.ChangeThemeAsync(theme, cancellationToken);
                }

                // Panellerin temasını güncelle;
                foreach (var panel in _panels.Values)
                {
                    await panel.ChangeThemeAsync(theme, cancellationToken);
                }

                CurrentTheme = theme;

                // Tema değişti olayı;
                await _eventBus.PublishAsync(new ThemeChangedEvent;
                {
                    Theme = theme,
                    DashboardId = GetDashboardId(),
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);

                _logger.LogInformation($"Dashboard theme changed to: {theme}");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "ChangeTheme",
                    AdditionalData = new { OldTheme = CurrentTheme, NewTheme = theme }
                });
                throw;
            }
        }

        /// <summary>
        /// Dashboard durum raporu oluşturur;
        /// </summary>
        public async Task<DashboardStatusReport> GetStatusReportAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var report = new DashboardStatusReport;
                {
                    DashboardId = GetDashboardId(),
                    Timestamp = DateTime.UtcNow,
                    State = State,
                    Theme = CurrentTheme,
                    LayoutId = CurrentLayoutId,
                    IsInitialized = IsInitialized,
                    LastRefreshTime = LastRefreshTime,
                    WidgetCount = _widgets.Count,
                    PanelCount = _panels.Count,
                    ActiveWidgets = _widgets.Values.Count(w => w.IsActive),
                    InactiveWidgets = _widgets.Values.Count(w => !w.IsActive),
                    HealthStatus = HealthStatus,
                    Metrics = Metrics,
                    UserSettings = UserSettings;
                };

                // Widget istatistikleri;
                report.WidgetStatistics = _widgets.Values;
                    .GroupBy(w => w.Type)
                    .Select(g => new WidgetTypeStatistics;
                    {
                        Type = g.Key,
                        Count = g.Count(),
                        ActiveCount = g.Count(w => w.IsActive)
                    })
                    .ToList();

                // Panel istatistikleri;
                report.PanelStatistics = _panels.Values;
                    .GroupBy(p => p.Type)
                    .Select(g => new PanelTypeStatistics;
                    {
                        Type = g.Key,
                        Count = g.Count(),
                        WidgetCount = g.Sum(p => p.Widgets.Count)
                    })
                    .ToList();

                // Performans metrikleri;
                report.PerformanceMetrics = await _performanceMonitor.GetDashboardMetricsAsync(cancellationToken);

                await Task.CompletedTask;
                return report;
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "Dashboard",
                    Operation = "GetStatusReport"
                });
                throw;
            }
        }

        #endregion;

        #region Private Methods;

        private async Task LoadDefaultWidgetsAsync(CancellationToken cancellationToken)
        {
            var defaultWidgets = new[]
            {
                new DashboardWidget;
                {
                    Id = "system_health",
                    Title = "Sistem Sağlığı",
                    Type = WidgetType.HealthMonitor,
                    Position = new WidgetPosition { Row = 0, Column = 0, Width = 6, Height = 4 },
                    Configuration = new WidgetConfiguration;
                    {
                        AutoRefresh = true,
                        RefreshInterval = TimeSpan.FromSeconds(30),
                        ShowTitle = true,
                        ShowBorder = true;
                    }
                },
                new DashboardWidget;
                {
                    Id = "performance_metrics",
                    Title = "Performans Metrikleri",
                    Type = WidgetType.PerformanceChart,
                    Position = new WidgetPosition { Row = 0, Column = 6, Width = 6, Height = 4 },
                    Configuration = new WidgetConfiguration;
                    {
                        AutoRefresh = true,
                        RefreshInterval = TimeSpan.FromSeconds(15),
                        ShowTitle = true,
                        ShowBorder = true;
                    }
                },
                new DashboardWidget;
                {
                    Id = "recent_activities",
                    Title = "Son Aktiviteler",
                    Type = WidgetType.ActivityLog,
                    Position = new WidgetPosition { Row = 4, Column = 0, Width = 12, Height = 4 },
                    Configuration = new WidgetConfiguration;
                    {
                        AutoRefresh = true,
                        RefreshInterval = TimeSpan.FromSeconds(60),
                        ShowTitle = true,
                        ShowBorder = true;
                    }
                }
            };

            foreach (var widget in defaultWidgets)
            {
                await AddWidgetAsync(widget, cancellationToken: cancellationToken);
            }
        }

        private async Task LoadDefaultPanelsAsync(CancellationToken cancellationToken)
        {
            var defaultPanels = new[]
            {
                new DashboardPanel;
                {
                    Id = "main_panel",
                    Title = "Ana Panel",
                    Type = PanelType.Main,
                    Position = new PanelPosition { Row = 0, Column = 0, Width = 12, Height = 8 },
                    Configuration = new PanelConfiguration;
                    {
                        IsCollapsible = false,
                        IsDraggable = true,
                        IsResizable = true,
                        ShowHeader = true;
                    }
                },
                new DashboardPanel;
                {
                    Id = "sidebar_panel",
                    Title = "Yan Panel",
                    Type = PanelType.Sidebar,
                    Position = new PanelPosition { Row = 0, Column = 12, Width = 4, Height = 12 },
                    Configuration = new PanelConfiguration;
                    {
                        IsCollapsible = true,
                        IsDraggable = true,
                        IsResizable = true,
                        ShowHeader = true;
                    }
                }
            };

            foreach (var panel in defaultPanels)
            {
                await AddPanelAsync(panel, cancellationToken);
            }
        }

        private async Task RefreshHealthStatusAsync(CancellationToken cancellationToken)
        {
            try
            {
                var healthResult = await _healthChecker.CheckSystemHealthAsync(cancellationToken);
                HealthStatus = new SystemHealthStatus;
                {
                    OverallStatus = healthResult.OverallStatus,
                    Components = healthResult.Components,
                    LastChecked = DateTime.UtcNow,
                    Message = healthResult.Message;
                };

                // Sağlık durumu değişikliği olayı;
                if (HealthStatus.OverallStatus != healthResult.OverallStatus)
                {
                    await _eventBus.PublishAsync(new HealthStatusChangedEvent;
                    {
                        DashboardId = GetDashboardId(),
                        OldStatus = HealthStatus.OverallStatus,
                        NewStatus = healthResult.OverallStatus,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to refresh health status: {ex.Message}");
                HealthStatus = new SystemHealthStatus;
                {
                    OverallStatus = HealthStatusType.Error,
                    LastChecked = DateTime.UtcNow,
                    Message = $"Health check failed: {ex.Message}"
                };
            }
        }

        private async Task RefreshMetricsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var metrics = await _performanceMonitor.GetSystemMetricsAsync(cancellationToken);

                Metrics = new DashboardMetrics;
                {
                    CpuUsage = metrics.CpuUsage,
                    MemoryUsage = metrics.MemoryUsage,
                    DiskUsage = metrics.DiskUsage,
                    NetworkIn = metrics.NetworkIn,
                    NetworkOut = metrics.NetworkOut,
                    ActiveProcesses = metrics.ActiveProcesses,
                    ResponseTime = metrics.ResponseTime,
                    ErrorRate = metrics.ErrorRate,
                    Timestamp = DateTime.UtcNow;
                };

                // Metrik değişikliği olayı;
                await _eventBus.PublishAsync(new MetricsUpdatedEvent;
                {
                    DashboardId = GetDashboardId(),
                    Metrics = Metrics,
                    Timestamp = DateTime.UtcNow;
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to refresh metrics: {ex.Message}");
            }
        }

        private async Task RefreshWidgetsAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            var refreshTasks = new List<Task>();

            foreach (var widget in _widgets.Values.Where(w => w.IsActive))
            {
                if (forceRefresh || widget.ShouldRefresh())
                {
                    refreshTasks.Add(RefreshWidgetAsync(widget, cancellationToken));
                }
            }

            if (refreshTasks.Count > 0)
            {
                await Task.WhenAll(refreshTasks);
            }
        }

        private async Task RefreshWidgetAsync(DashboardWidget widget, CancellationToken cancellationToken)
        {
            try
            {
                await widget.RefreshAsync(cancellationToken);
                widget.LastRefreshTime = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to refresh widget '{widget.Title}': {ex.Message}");
                widget.Status = WidgetStatus.Error;
            }
        }

        private async Task RefreshPanelsAsync(CancellationToken cancellationToken)
        {
            foreach (var panel in _panels.Values.Where(p => p.IsVisible))
            {
                try
                {
                    await panel.RefreshAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to refresh panel '{panel.Title}': {ex.Message}");
                }
            }
        }

        private async Task CleanupWidgetsAsync()
        {
            var cleanupTasks = _widgets.Values.Select(w => w.CleanupAsync()).ToList();
            await Task.WhenAll(cleanupTasks);

            _widgets.Clear();
            await ExecuteOnUiThreadAsync(() => Widgets.Clear());
        }

        private async Task CleanupPanelsAsync()
        {
            var cleanupTasks = _panels.Values.Select(p => p.CleanupAsync()).ToList();
            await Task.WhenAll(cleanupTasks);

            _panels.Clear();
            await ExecuteOnUiThreadAsync(() => Panels.Clear());
        }

        private async Task LoadUserSettingsAsync(string userId, CancellationToken cancellationToken)
        {
            try
            {
                // Burada kullanıcı ayarları veritabanından veya dosyadan yüklenir;
                // Şimdilik varsayılan ayarlar kullanılıyor;
                UserSettings = new DashboardUserSettings;
                {
                    UserId = userId,
                    AutoRefreshEnabled = true,
                    RefreshInterval = TimeSpan.FromMinutes(5),
                    Theme = DashboardTheme.Default,
                    DefaultLayout = "default",
                    WidgetSettings = new Dictionary<string, WidgetUserSettings>(),
                    NotificationsEnabled = true,
                    SoundEnabled = false;
                };

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to load user settings: {ex.Message}");
                UserSettings = DashboardUserSettings.Default;
            }
        }

        private async Task AutoRefreshAsync()
        {
            if (State != DashboardState.Running || !UserSettings.AutoRefreshEnabled)
                return;

            try
            {
                await RefreshAsync(forceFullRefresh: false, _dashboardCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Auto refresh failed: {ex.Message}");
            }
        }

        private async Task UpdateMetricsAsync()
        {
            if (State != DashboardState.Running)
                return;

            try
            {
                await RefreshMetricsAsync(_dashboardCts?.Token ?? CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Metrics update failed: {ex.Message}");
            }
        }

        private void StartTimers()
        {
            if (UserSettings.AutoRefreshEnabled)
            {
                _autoRefreshTimer.Interval = UserSettings.RefreshInterval.TotalMilliseconds;
                _autoRefreshTimer.Start();
            }

            _metricsUpdateTimer.Start();
        }

        private void StopTimers()
        {
            _autoRefreshTimer.Stop();
            _metricsUpdateTimer.Stop();
        }

        private ValidationResult ValidateWidget(DashboardWidget widget)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(widget.Id))
                errors.Add("Widget ID cannot be empty");

            if (string.IsNullOrWhiteSpace(widget.Title))
                errors.Add("Widget title cannot be empty");

            if (widget.Position == null)
                errors.Add("Widget position cannot be null");

            if (widget.Position.Width <= 0 || widget.Position.Height <= 0)
                errors.Add("Widget dimensions must be positive");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private ValidationResult ValidatePanel(DashboardPanel panel)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(panel.Id))
                errors.Add("Panel ID cannot be empty");

            if (string.IsNullOrWhiteSpace(panel.Title))
                errors.Add("Panel title cannot be empty");

            if (panel.Position == null)
                errors.Add("Panel position cannot be null");

            if (panel.Position.Width <= 0 || panel.Position.Height <= 0)
                errors.Add("Panel dimensions must be positive");

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private async Task<DashboardLayout> LoadLayoutAsync(string layoutId, CancellationToken cancellationToken)
        {
            // Burada layout veritabanından veya dosyadan yüklenir;
            // Şimdilik varsayılan layout döndürülüyor;

            await Task.CompletedTask;

            return new DashboardLayout;
            {
                Id = layoutId,
                Name = "Default Layout",
                Description = "Varsayılan dashboard layout'u",
                CreatedAt = DateTime.UtcNow,
                Panels = new List<PanelConfiguration>(),
                Widgets = new List<WidgetConfiguration>()
            };
        }

        private DashboardWidget CreateWidgetFromConfig(WidgetConfiguration config)
        {
            return new DashboardWidget;
            {
                Id = config.Id,
                Title = config.Title,
                Type = config.Type,
                Position = config.Position,
                Configuration = config;
            };
        }

        private DashboardPanel CreatePanelFromConfig(PanelConfiguration config)
        {
            return new DashboardPanel;
            {
                Id = config.Id,
                Title = config.Title,
                Type = config.Type,
                Position = config.Position,
                Configuration = config;
            };
        }

        private async Task<double> MeasureExecutionTimeAsync(Func<Task> action)
        {
            var startTime = DateTime.UtcNow;
            await action();
            return (DateTime.UtcNow - startTime).TotalMilliseconds;
        }

        private async Task ExecuteOnUiThreadAsync(Action action)
        {
            // WPF/MVVM uygulamalarında UI thread üzerinde çalıştırmak için;
            // Gerçek implementasyonda Dispatcher kullanılır;
            try
            {
                // Bu basit implementasyon async/await ile thread havuzunda çalışır;
                // Gerçek projede Dispatcher.InvokeAsync kullanılmalı;
                await Task.Run(() =>
                {
                    // Simüle edilmiş UI thread işlemi;
                    action();
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"UI thread execution failed: {ex.Message}");
            }
        }

        private string GetDashboardId()
        {
            return $"dashboard_{Environment.MachineName}_{GetHashCode()}";
        }

        private void SubscribeToEvents()
        {
            // Sistem olaylarına abone ol;
            _eventBus.Subscribe<SystemEvent>(HandleSystemEvent);
            _eventBus.Subscribe<PerformanceAlertEvent>(HandlePerformanceAlert);
            _eventBus.Subscribe<HealthAlertEvent>(HandleHealthAlert);
        }

        private void UnsubscribeFromEvents()
        {
            _eventBus.Unsubscribe<SystemEvent>(HandleSystemEvent);
            _eventBus.Unsubscribe<PerformanceAlertEvent>(HandlePerformanceAlert);
            _eventBus.Unsubscribe<HealthAlertEvent>(HandleHealthAlert);
        }

        private async Task HandleSystemEvent(SystemEvent systemEvent)
        {
            // Sistem olaylarını işle;
            _logger.LogInformation($"System event received: {systemEvent.EventType}");

            // Dashboard'ı güncelle;
            if (State == DashboardState.Running)
            {
                await RefreshMetricsAsync(_dashboardCts?.Token ?? CancellationToken.None);
            }
        }

        private async Task HandlePerformanceAlert(PerformanceAlertEvent alert)
        {
            _logger.LogWarning($"Performance alert: {alert.Message} (Level: {alert.AlertLevel})");

            // Performans widget'ını güncelle;
            if (_widgets.TryGetValue("performance_metrics", out var widget))
            {
                await widget.HandleAlertAsync(alert, _dashboardCts?.Token ?? CancellationToken.None);
            }
        }

        private async Task HandleHealthAlert(HealthAlertEvent alert)
        {
            _logger.LogWarning($"Health alert: {alert.Message} (Component: {alert.Component})");

            // Sağlık widget'ını güncelle;
            if (_widgets.TryGetValue("system_health", out var widget))
            {
                await widget.HandleAlertAsync(alert, _dashboardCts?.Token ?? CancellationToken.None);
            }
        }

        private void OnDashboardPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // Property değişikliklerini işle;
            switch (e.PropertyName)
            {
                case nameof(UserSettings):
                    OnUserSettingsChanged();
                    break;
                case nameof(CurrentTheme):
                    OnThemeChanged();
                    break;
                case nameof(State):
                    OnStateChanged();
                    break;
            }
        }

        private void OnUserSettingsChanged()
        {
            // Kullanıcı ayarları değiştiğinde timer'ları güncelle;
            if (_autoRefreshTimer != null)
            {
                _autoRefreshTimer.Interval = UserSettings.RefreshInterval.TotalMilliseconds;
                _autoRefreshTimer.Enabled = UserSettings.AutoRefreshEnabled;
            }
        }

        private void OnThemeChanged()
        {
            // Tema değişikliğini widget'lara ve panel'lere yay;
            // Bu işlem asenkron olarak yapılmalı, ancak event handler async olamaz;
            // Bu nedenle fire-and-forget pattern kullanılıyor;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ChangeThemeAsync(CurrentTheme, _dashboardCts?.Token ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to apply theme change: {ex.Message}");
                }
            });
        }

        private void OnStateChanged()
        {
            _logger.LogInformation($"Dashboard state changed to: {State}");
        }

        #endregion;

        #region INotifyPropertyChanged Implementation;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dashboard'ı durdur;
                    _ = ShutdownAsync().ConfigureAwait(false);

                    // Timer'ları dispose et;
                    _autoRefreshTimer?.Dispose();
                    _metricsUpdateTimer?.Dispose();

                    // CancellationTokenSource'ı dispose et;
                    _dashboardCts?.Dispose();

                    // Olay aboneliklerini temizle;
                    PropertyChanged -= OnDashboardPropertyChanged;
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
    /// Dashboard widget türleri;
    /// </summary>
    public enum WidgetType;
    {
        HealthMonitor,
        PerformanceChart,
        ActivityLog,
        ResourceMonitor,
        AlertPanel,
        SystemStatus,
        Custom;
    }

    /// <summary>
    /// Panel türleri;
    /// </summary>
    public enum PanelType;
    {
        Main,
        Sidebar,
        Header,
        Footer,
        Custom;
    }

    /// <summary>
    /// Dashboard durumları;
    /// </summary>
    public enum DashboardState;
    {
        Stopped,
        Initializing,
        Running,
        Refreshing,
        ShuttingDown,
        Error;
    }

    /// <summary>
    /// Dashboard temaları;
    /// </summary>
    public enum DashboardTheme;
    {
        Default,
        Dark,
        Light,
        HighContrast,
        Custom;
    }

    /// <summary>
    /// Widget durumları;
    /// </summary>
    public enum WidgetStatus;
    {
        Initializing,
        Active,
        Inactive,
        Error,
        Loading,
        Refreshing;
    }

    /// <summary>
    /// Sağlık durumu türleri;
    /// </summary>
    public enum HealthStatusType;
    {
        Healthy,
        Warning,
        Error,
        Unknown;
    }

    /// <summary>
    /// Dashboard widget'ı;
    /// </summary>
    public class DashboardWidget : INotifyPropertyChanged;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public WidgetType Type { get; set; }
        public string PanelId { get; set; }
        public WidgetPosition Position { get; set; }
        public WidgetConfiguration Configuration { get; set; }

        private WidgetStatus _status = WidgetStatus.Initializing;
        public WidgetStatus Status;
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        private bool _isActive = true;
        public bool IsActive;
        {
            get => _isActive;
            set => SetField(ref _isActive, value);
        }

        private DateTime _lastRefreshTime;
        public DateTime LastRefreshTime;
        {
            get => _lastRefreshTime;
            set => SetField(ref _lastRefreshTime, value);
        }

        private object _data;
        public object Data;
        {
            get => _data;
            set => SetField(ref _data, value);
        }

        private string _errorMessage;
        public string ErrorMessage;
        {
            get => _errorMessage;
            set => SetField(ref _errorMessage, value);
        }

        public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            Status = WidgetStatus.Active;
            LastRefreshTime = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public virtual Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            // Türetilen sınıflar bu metodu override eder;
            return Task.CompletedTask;
        }

        public virtual Task CleanupAsync()
        {
            Status = WidgetStatus.Inactive;
            return Task.CompletedTask;
        }

        public virtual Task ChangeThemeAsync(DashboardTheme theme, CancellationToken cancellationToken = default)
        {
            // Türetilen sınıflar bu metodu override eder;
            return Task.CompletedTask;
        }

        public virtual Task HandleAlertAsync(object alert, CancellationToken cancellationToken = default)
        {
            // Türetilen sınıflar bu metodu override eder;
            return Task.CompletedTask;
        }

        public bool ShouldRefresh()
        {
            if (Configuration == null || !Configuration.AutoRefresh)
                return false;

            var timeSinceLastRefresh = DateTime.UtcNow - LastRefreshTime;
            return timeSinceLastRefresh >= Configuration.RefreshInterval;
        }

        #region INotifyPropertyChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        #endregion;
    }

    /// <summary>
    /// Dashboard panel'i;
    /// </summary>
    public class DashboardPanel : INotifyPropertyChanged;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public PanelType Type { get; set; }
        public PanelPosition Position { get; set; }
        public PanelConfiguration Configuration { get; set; }

        private bool _isVisible = true;
        public bool IsVisible;
        {
            get => _isVisible;
            set => SetField(ref _isVisible, value);
        }

        private bool _isExpanded = true;
        public bool IsExpanded;
        {
            get => _isExpanded;
            set => SetField(ref _isExpanded, value);
        }

        private ObservableCollection<DashboardWidget> _widgets = new ObservableCollection<DashboardWidget>();
        public ObservableCollection<DashboardWidget> Widgets;
        {
            get => _widgets;
            set => SetField(ref _widgets, value);
        }

        public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public virtual Task RefreshAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public virtual Task CleanupAsync()
        {
            Widgets.Clear();
            return Task.CompletedTask;
        }

        public virtual Task ChangeThemeAsync(DashboardTheme theme, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        #region INotifyPropertyChanged;
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        protected bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
        #endregion;
    }

    /// <summary>
    /// Dashboard metrikleri;
    /// </summary>
    public class DashboardMetrics;
    {
        public double CpuUsage { get; set; }
        public double MemoryUsage { get; set; }
        public double DiskUsage { get; set; }
        public double NetworkIn { get; set; }
        public double NetworkOut { get; set; }
        public int ActiveProcesses { get; set; }
        public double ResponseTime { get; set; }
        public double ErrorRate { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sistem sağlık durumu;
    /// </summary>
    public class SystemHealthStatus;
    {
        public HealthStatusType OverallStatus { get; set; }
        public Dictionary<string, HealthStatusType> Components { get; set; } = new Dictionary<string, HealthStatusType>();
        public DateTime LastChecked { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// Dashboard kullanıcı ayarları;
    /// </summary>
    public class DashboardUserSettings;
    {
        public static DashboardUserSettings Default => new DashboardUserSettings();

        public string UserId { get; set; }
        public bool AutoRefreshEnabled { get; set; } = true;
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
        public DashboardTheme Theme { get; set; } = DashboardTheme.Default;
        public string DefaultLayout { get; set; } = "default";
        public Dictionary<string, WidgetUserSettings> WidgetSettings { get; set; } = new Dictionary<string, WidgetUserSettings>();
        public bool NotificationsEnabled { get; set; } = true;
        public bool SoundEnabled { get; set; } = false;
    }

    /// <summary>
    /// Widget kullanıcı ayarları;
    /// </summary>
    public class WidgetUserSettings;
    {
        public bool IsVisible { get; set; } = true;
        public WidgetPosition Position { get; set; }
        public Dictionary<string, object> CustomSettings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Widget pozisyonu;
    /// </summary>
    public class WidgetPosition;
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public int Width { get; set; } = 1;
        public int Height { get; set; } = 1;
    }

    /// <summary>
    /// Panel pozisyonu;
    /// </summary>
    public class PanelPosition;
    {
        public int Row { get; set; }
        public int Column { get; set; }
        public int Width { get; set; } = 12;
        public int Height { get; set; } = 8;
    }

    /// <summary>
    /// Widget konfigürasyonu;
    /// </summary>
    public class WidgetConfiguration;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public WidgetType Type { get; set; }
        public string PanelId { get; set; }
        public WidgetPosition Position { get; set; }
        public bool AutoRefresh { get; set; } = true;
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
        public bool ShowTitle { get; set; } = true;
        public bool ShowBorder { get; set; } = true;
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Panel konfigürasyonu;
    /// </summary>
    public class PanelConfiguration;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public PanelType Type { get; set; }
        public PanelPosition Position { get; set; }
        public bool IsCollapsible { get; set; } = true;
        public bool IsDraggable { get; set; } = true;
        public bool IsResizable { get; set; } = true;
        public bool ShowHeader { get; set; } = true;
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dashboard layout'u;
    /// </summary>
    public class DashboardLayout;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<PanelConfiguration> Panels { get; set; } = new List<PanelConfiguration>();
        public List<WidgetConfiguration> Widgets { get; set; } = new List<WidgetConfiguration>();
    }

    /// <summary>
    /// Dashboard konfigürasyonu;
    /// </summary>
    public class DashboardConfiguration;
    {
        public static DashboardConfiguration Default => new DashboardConfiguration();

        public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan MetricsUpdateInterval { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxWidgetsPerPanel { get; set; } = 10;
        public int MaxPanels { get; set; } = 5;
        public bool EnableCaching { get; set; } = true;
        public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(30);
        public bool EnablePerformanceMonitoring { get; set; } = true;
        public bool EnableHealthChecks { get; set; } = true;
    }

    /// <summary>
    /// Yenileme sonucu;
    /// </summary>
    public class RefreshResult;
    {
        public string RefreshId { get; set; }
        public bool IsSuccess { get; set; }
        public double RefreshTime { get; set; }
        public RefreshStatistics Statistics { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Yenileme istatistikleri;
    /// </summary>
    public class RefreshStatistics;
    {
        public double HealthCheckTime { get; set; }
        public double MetricsUpdateTime { get; set; }
        public double WidgetRefreshTime { get; set; }
        public double PanelRefreshTime { get; set; }
        public double TotalTime { get; set; }
    }

    /// <summary>
    /// Widget operasyon sonucu;
    /// </summary>
    public class WidgetOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string WidgetId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static WidgetOperationResult Success(string widgetId, string message = null)
        {
            return new WidgetOperationResult;
            {
                IsSuccess = true,
                WidgetId = widgetId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static WidgetOperationResult Failure(string error)
        {
            return new WidgetOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Panel operasyon sonucu;
    /// </summary>
    public class PanelOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string PanelId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static PanelOperationResult Success(string panelId, string message = null)
        {
            return new PanelOperationResult;
            {
                IsSuccess = true,
                PanelId = panelId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static PanelOperationResult Failure(string error)
        {
            return new PanelOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Layout operasyon sonucu;
    /// </summary>
    public class LayoutOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string LayoutId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static LayoutOperationResult Success(string layoutId, string message = null)
        {
            return new LayoutOperationResult;
            {
                IsSuccess = true,
                LayoutId = layoutId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static LayoutOperationResult Failure(string error)
        {
            return new LayoutOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Dashboard durum raporu;
    /// </summary>
    public class DashboardStatusReport;
    {
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
        public DashboardState State { get; set; }
        public DashboardTheme Theme { get; set; }
        public string LayoutId { get; set; }
        public bool IsInitialized { get; set; }
        public DateTime LastRefreshTime { get; set; }
        public int WidgetCount { get; set; }
        public int PanelCount { get; set; }
        public int ActiveWidgets { get; set; }
        public int InactiveWidgets { get; set; }
        public SystemHealthStatus HealthStatus { get; set; }
        public DashboardMetrics Metrics { get; set; }
        public DashboardUserSettings UserSettings { get; set; }
        public List<WidgetTypeStatistics> WidgetStatistics { get; set; } = new List<WidgetTypeStatistics>();
        public List<PanelTypeStatistics> PanelStatistics { get; set; } = new List<PanelTypeStatistics>();
        public object PerformanceMetrics { get; set; }
    }

    /// <summary>
    /// Widget tipi istatistikleri;
    /// </summary>
    public class WidgetTypeStatistics;
    {
        public WidgetType Type { get; set; }
        public int Count { get; set; }
        public int ActiveCount { get; set; }
    }

    /// <summary>
    /// Panel tipi istatistikleri;
    /// </summary>
    public class PanelTypeStatistics;
    {
        public PanelType Type { get; set; }
        public int Count { get; set; }
        public int WidgetCount { get; set; }
    }

    /// <summary>
    /// Doğrulama sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Dashboard başlatıldı olayı;
    /// </summary>
    public class DashboardInitializedEvent : IEvent;
    {
        public string DashboardId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public int WidgetCount { get; set; }
        public int PanelCount { get; set; }
    }

    /// <summary>
    /// Dashboard durduruldu olayı;
    /// </summary>
    public class DashboardShutdownEvent : IEvent;
    {
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Dashboard yenileme başladı olayı;
    /// </summary>
    public class DashboardRefreshStartedEvent : IEvent;
    {
        public string RefreshId { get; set; }
        public string DashboardId { get; set; }
        public bool IsFullRefresh { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Dashboard yenileme tamamlandı olayı;
    /// </summary>
    public class DashboardRefreshCompletedEvent : IEvent;
    {
        public string RefreshId { get; set; }
        public string DashboardId { get; set; }
        public bool IsSuccess { get; set; }
        public double RefreshTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Dashboard yenileme başarısız oldu olayı;
    /// </summary>
    public class DashboardRefreshFailedEvent : IEvent;
    {
        public string RefreshId { get; set; }
        public string DashboardId { get; set; }
        public string Error { get; set; }
        public double RefreshTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget eklendi olayı;
    /// </summary>
    public class WidgetAddedEvent : IEvent;
    {
        public string WidgetId { get; set; }
        public WidgetType WidgetType { get; set; }
        public string PanelId { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget kaldırıldı olayı;
    /// </summary>
    public class WidgetRemovedEvent : IEvent;
    {
        public string WidgetId { get; set; }
        public WidgetType WidgetType { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Widget güncellendi olayı;
    /// </summary>
    public class WidgetUpdatedEvent : IEvent;
    {
        public string WidgetId { get; set; }
        public WidgetType WidgetType { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Panel eklendi olayı;
    /// </summary>
    public class PanelAddedEvent : IEvent;
    {
        public string PanelId { get; set; }
        public PanelType PanelType { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Layout değiştirildi olayı;
    /// </summary>
    public class LayoutChangedEvent : IEvent;
    {
        public string LayoutId { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tema değiştiriliyor olayı;
    /// </summary>
    public class ThemeChangingEvent : IEvent;
    {
        public DashboardTheme OldTheme { get; set; }
        public DashboardTheme NewTheme { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tema değiştirildi olayı;
    /// </summary>
    public class ThemeChangedEvent : IEvent;
    {
        public DashboardTheme Theme { get; set; }
        public string DashboardId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sağlık durumu değişti olayı;
    /// </summary>
    public class HealthStatusChangedEvent : IEvent;
    {
        public string DashboardId { get; set; }
        public HealthStatusType OldStatus { get; set; }
        public HealthStatusType NewStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Metrikler güncellendi olayı;
    /// </summary>
    public class MetricsUpdatedEvent : IEvent;
    {
        public string DashboardId { get; set; }
        public DashboardMetrics Metrics { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sistem olayı;
    /// </summary>
    public class SystemEvent : IEvent;
    {
        public string EventType { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Performans uyarı olayı;
    /// </summary>
    public class PerformanceAlertEvent : IEvent;
    {
        public string AlertLevel { get; set; }
        public string Message { get; set; }
        public string Metric { get; set; }
        public double Value { get; set; }
        public double Threshold { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Sağlık uyarı olayı;
    /// </summary>
    public class HealthAlertEvent : IEvent;
    {
        public string Component { get; set; }
        public HealthStatusType Status { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;
}

namespace NEDA.Interface.VisualInterface.MainDashboard.Contracts;
{
    /// <summary>
    /// Dashboard servisi interface'i;
    /// </summary>
    public interface IDashboard : INotifyPropertyChanged, IDisposable;
    {
        // Properties;
        string Title { get; set; }
        string Description { get; set; }
        string Version { get; set; }
        DashboardState State { get; }
        DashboardTheme CurrentTheme { get; }
        string CurrentLayoutId { get; }
        ObservableCollection<DashboardWidget> Widgets { get; }
        ObservableCollection<DashboardPanel> Panels { get; }
        DashboardMetrics Metrics { get; }
        SystemHealthStatus HealthStatus { get; }
        DateTime LastRefreshTime { get; }
        bool IsInitialized { get; }
        DashboardUserSettings UserSettings { get; set; }

        // Methods;
        Task InitializeAsync(string userId, CancellationToken cancellationToken = default);
        Task ShutdownAsync();
        Task<RefreshResult> RefreshAsync(bool forceFullRefresh = false, CancellationToken cancellationToken = default);

        Task<WidgetOperationResult> AddWidgetAsync(
            DashboardWidget widget,
            string panelId = null,
            CancellationToken cancellationToken = default);

        Task<WidgetOperationResult> RemoveWidgetAsync(
            string widgetId,
            CancellationToken cancellationToken = default);

        Task<WidgetOperationResult> UpdateWidgetAsync(
            DashboardWidget updatedWidget,
            CancellationToken cancellationToken = default);

        Task<PanelOperationResult> AddPanelAsync(
            DashboardPanel panel,
            CancellationToken cancellationToken = default);

        Task<LayoutOperationResult> ChangeLayoutAsync(
            string layoutId,
            CancellationToken cancellationToken = default);

        Task ChangeThemeAsync(
            DashboardTheme theme,
            CancellationToken cancellationToken = default);

        Task<DashboardStatusReport> GetStatusReportAsync(
            CancellationToken cancellationToken = default);
    }
}
