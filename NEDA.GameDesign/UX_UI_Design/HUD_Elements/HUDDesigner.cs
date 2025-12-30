using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.GameDesign.UX_UI_Design.Animation;
using NEDA.GameDesign.UX_UI_Design.Configuration;
using NEDA.GameDesign.UX_UI_Design.DataModels;
using NEDA.GameDesign.UX_UI_Design.Layout;
using NEDA.GameDesign.UX_UI_Design.Rendering;
using NEDA.GameDesign.UX_UI_Design.Theming;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace NEDA.GameDesign.UX_UI_Design.HUD_Elements;
{
    /// <summary>
    /// HUD (Heads-Up Display) tasarımı, yönetimi ve render işlemlerini yöneten ana sınıf.
    /// Endüstriyel seviyede HUD sistemleri için gelişmiş özellikler sağlar.
    /// </summary>
    public class HUDDesigner : IHUDDesigner, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IHUDAnimator _hudAnimator;
        private readonly IHUDThemeManager _themeManager;
        private readonly IHUDLayoutEngine _layoutEngine;
        private readonly IHUDWidgetFactory _widgetFactory;
        private readonly HUDConfiguration _configuration;

        private readonly Dictionary<string, HUDWidget> _widgets;
        private readonly Dictionary<string, HUDLayer> _layers;
        private readonly Canvas _hudCanvas;
        private readonly DispatcherTimer _updateTimer;

        private HUDState _currentState;
        private bool _isInitialized;
        private bool _isRendering;
        private bool _isDesignMode;
        private readonly object _renderLock = new object();

        /// <summary>
        /// Aktif HUD widget'ları;
        /// </summary>
        public IReadOnlyDictionary<string, HUDWidget> Widgets => _widgets;

        /// <summary>
        /// HUD katmanları;
        /// </summary>
        public IReadOnlyDictionary<string, HUDLayer> Layers => _layers;

        /// <summary>
        /// HUD durumu;
        /// </summary>
        public HUDState CurrentState;
        {
            get => _currentState;
            private set;
            {
                if (_currentState != value)
                {
                    var oldState = _currentState;
                    _currentState = value;
                    OnHUDStateChanged(new HUDStateChangedEventArgs(oldState, value));
                }
            }
        }

        /// <summary>
        /// HUD istatistikleri;
        /// </summary>
        public HUDStatistics Statistics { get; private set; }

        /// <summary>
        /// HUD canvas'ı;
        /// </summary>
        public Canvas HUDElement => _hudCanvas;

        #endregion;

        #region Events;

        /// <summary>
        /// HUD başlatıldığında tetiklenir;
        /// </summary>
        public event EventHandler<HUDInitializedEventArgs> HUDInitialized;

        /// <summary>
        /// HUD durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<HUDStateChangedEventArgs> HUDStateChanged;

        /// <summary>
        /// Widget eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<WidgetEventArgs> WidgetAdded;

        /// <summary>
        /// Widget kaldırıldığında tetiklenir;
        /// </summary>
        public event EventHandler<WidgetEventArgs> WidgetRemoved;

        /// <summary>
        /// Widget güncellendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<WidgetUpdatedEventArgs> WidgetUpdated;

        /// <summary>
        /// HUD render edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<HUDRenderEventArgs> HUDRendered;

        #endregion;

        #region Constructor;

        /// <summary>
        /// HUDDesigner sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="hudAnimator">HUD animasyon motoru</param>
        /// <param name="themeManager">HUD tema yöneticisi</param>
        /// <param name="layoutEngine">HUD layout motoru</param>
        /// <param name="widgetFactory">HUD widget fabrikası</param>
        /// <param name="configuration">HUD konfigürasyonu</param>
        public HUDDesigner(
            ILogger logger,
            IHUDAnimator hudAnimator,
            IHUDThemeManager themeManager,
            IHUDLayoutEngine layoutEngine,
            IHUDWidgetFactory widgetFactory,
            HUDConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hudAnimator = hudAnimator ?? throw new ArgumentNullException(nameof(hudAnimator));
            _themeManager = themeManager ?? throw new ArgumentNullException(nameof(themeManager));
            _layoutEngine = layoutEngine ?? throw new ArgumentNullException(nameof(layoutEngine));
            _widgetFactory = widgetFactory ?? throw new ArgumentNullException(nameof(widgetFactory));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _widgets = new Dictionary<string, HUDWidget>();
            _layers = new Dictionary<string, HUDLayer>();
            _hudCanvas = new Canvas();
            _updateTimer = new DispatcherTimer();

            Initialize();
        }

        #endregion;

        #region Initialization;

        private void Initialize()
        {
            try
            {
                _logger.LogInformation("HUDDesigner başlatılıyor...");

                CurrentState = HUDState.Initializing;
                Statistics = new HUDStatistics();

                // Canvas özelliklerini ayarla;
                ConfigureCanvas();

                // Update timer'ı ayarla;
                ConfigureUpdateTimer();

                // Tema yöneticisini başlat;
                InitializeThemeManager();

                // Layout motorunu başlat;
                InitializeLayoutEngine();

                // Varsayılan katmanları oluştur;
                CreateDefaultLayers();

                CurrentState = HUDState.Ready;
                _isInitialized = true;

                OnHUDInitialized(new HUDInitializedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    WidgetCount = _widgets.Count,
                    LayerCount = _layers.Count;
                });

                _logger.LogInformation("HUDDesigner başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUDDesigner başlatma sırasında hata oluştu.");
                throw new HUDDesignerException("HUDDesigner başlatılamadı.", ex);
            }
        }

        private void ConfigureCanvas()
        {
            _hudCanvas.Width = _configuration.CanvasWidth;
            _hudCanvas.Height = _configuration.CanvasHeight;
            _hudCanvas.Background = Brushes.Transparent;
            _hudCanvas.IsHitTestVisible = _configuration.IsInteractive;
            _hudCanvas.Focusable = _configuration.IsInteractive;

            // Render transform ayarla;
            if (_configuration.EnableRenderTransform)
            {
                var transformGroup = new TransformGroup();
                if (_configuration.Scale != 1.0)
                {
                    transformGroup.Children.Add(new ScaleTransform(_configuration.Scale, _configuration.Scale));
                }
                if (_configuration.Rotation != 0)
                {
                    transformGroup.Children.Add(new RotateTransform(_configuration.Rotation));
                }
                _hudCanvas.RenderTransform = transformGroup;
            }
        }

        private void ConfigureUpdateTimer()
        {
            _updateTimer.Interval = TimeSpan.FromMilliseconds(_configuration.UpdateIntervalMs);
            _updateTimer.Tick += OnUpdateTimerTick;

            if (_configuration.AutoUpdate)
            {
                _updateTimer.Start();
            }
        }

        private void InitializeThemeManager()
        {
            _themeManager.Initialize(_configuration.ThemeSettings);

            // Tema değişikliklerini dinle;
            _themeManager.ThemeChanged += OnThemeChanged;
        }

        private void InitializeLayoutEngine()
        {
            _layoutEngine.Initialize(_configuration.LayoutSettings);

            // Layout motoruna canvas'ı bağla;
            _layoutEngine.SetCanvas(_hudCanvas);
        }

        private void CreateDefaultLayers()
        {
            // Z-order'a göre katmanlar oluştur;
            var layers = new[]
            {
                new HUDLayer;
                {
                    Id = "Background",
                    Name = "Background Layer",
                    ZIndex = 0,
                    IsVisible = true,
                    Opacity = 1.0,
                    CanReceiveInput = false;
                },
                new HUDLayer;
                {
                    Id = "GameInfo",
                    Name = "Game Information",
                    ZIndex = 10,
                    IsVisible = true,
                    Opacity = 1.0,
                    CanReceiveInput = true;
                },
                new HUDLayer;
                {
                    Id = "PlayerInfo",
                    Name = "Player Information",
                    ZIndex = 20,
                    IsVisible = true,
                    Opacity = 1.0,
                    CanReceiveInput = true;
                },
                new HUDLayer;
                {
                    Id = "Notifications",
                    Name = "Notifications",
                    ZIndex = 30,
                    IsVisible = true,
                    Opacity = 1.0,
                    CanReceiveInput = true;
                },
                new HUDLayer;
                {
                    Id = "Overlay",
                    Name = "Overlay Elements",
                    ZIndex = 40,
                    IsVisible = true,
                    Opacity = 0.8,
                    CanReceiveInput = false;
                }
            };

            foreach (var layer in layers)
            {
                AddLayerInternal(layer);
            }
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// HUD'u başlatır ve render etmeye başlar;
        /// </summary>
        public void StartHUD()
        {
            if (!_isInitialized)
                throw new HUDDesignerException("HUDDesigner başlatılmamış.");

            if (CurrentState == HUDState.Running)
                return;

            try
            {
                _logger.LogInformation("HUD başlatılıyor...");

                CurrentState = HUDState.Starting;

                // Update timer'ı başlat;
                if (!_updateTimer.IsEnabled && _configuration.AutoUpdate)
                {
                    _updateTimer.Start();
                }

                // Tüm widget'ları görünür yap;
                foreach (var widget in _widgets.Values)
                {
                    widget.IsVisible = true;
                }

                // Tüm katmanları görünür yap;
                foreach (var layer in _layers.Values)
                {
                    layer.IsVisible = true;
                }

                CurrentState = HUDState.Running;

                _logger.LogInformation("HUD başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUD başlatma sırasında hata oluştu.");
                throw new HUDDesignerException("HUD başlatılamadı.", ex);
            }
        }

        /// <summary>
        /// HUD'u durdurur;
        /// </summary>
        public void StopHUD()
        {
            if (CurrentState == HUDState.Stopped)
                return;

            try
            {
                _logger.LogInformation("HUD durduruluyor...");

                CurrentState = HUDState.Stopping;

                // Update timer'ı durdur;
                if (_updateTimer.IsEnabled)
                {
                    _updateTimer.Stop();
                }

                // Tüm widget'ları gizle;
                foreach (var widget in _widgets.Values)
                {
                    widget.IsVisible = false;
                }

                CurrentState = HUDState.Stopped;

                _logger.LogInformation("HUD durduruldu.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUD durdurma sırasında hata oluştu.");
                throw new HUDDesignerException("HUD durdurulamadı.", ex);
            }
        }

        /// <summary>
        /// Yeni bir widget ekler;
        /// </summary>
        /// <param name="widgetConfig">Widget konfigürasyonu</param>
        /// <returns>Eklenen widget</returns>
        public async Task<HUDWidget> AddWidgetAsync(WidgetConfiguration widgetConfig)
        {
            if (!_isInitialized)
                throw new HUDDesignerException("HUDDesigner başlatılmamış.");

            try
            {
                _logger.LogInformation($"Widget ekleniyor: {widgetConfig.WidgetId}");

                // Widget oluştur;
                var widget = await _widgetFactory.CreateWidgetAsync(widgetConfig);

                // Katmanı kontrol et;
                if (!_layers.ContainsKey(widgetConfig.LayerId))
                {
                    throw new HUDDesignerException($"Belirtilen katman bulunamadı: {widgetConfig.LayerId}");
                }

                // Widget'ı ekle;
                AddWidgetInternal(widget, widgetConfig.LayerId);

                // Layout motoruna widget'ı ekle;
                await _layoutEngine.AddWidgetAsync(widget);

                // Animasyon uygula (eğer varsa)
                if (widgetConfig.AnimationConfig != null)
                {
                    await _hudAnimator.AnimateWidgetAsync(widget, widgetConfig.AnimationConfig);
                }

                // WidgetAdded event'ini tetikle;
                OnWidgetAdded(new WidgetEventArgs;
                {
                    WidgetId = widget.Id,
                    WidgetType = widget.WidgetType,
                    LayerId = widgetConfig.LayerId,
                    Timestamp = DateTime.UtcNow;
                });

                UpdateStatistics();

                _logger.LogInformation($"Widget eklendi: {widgetConfig.WidgetId}");

                return widget;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Widget ekleme sırasında hata oluştu: {widgetConfig.WidgetId}");
                throw new HUDDesignerException($"Widget eklenemedi: {widgetConfig.WidgetId}", ex);
            }
        }

        /// <summary>
        /// Widget'ı kaldırır;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        public async Task RemoveWidgetAsync(string widgetId)
        {
            if (!_widgets.ContainsKey(widgetId))
                return;

            try
            {
                _logger.LogInformation($"Widget kaldırılıyor: {widgetId}");

                var widget = _widgets[widgetId];

                // Animasyonu durdur;
                await _hudAnimator.StopAnimationsAsync(widget);

                // Layout motorundan kaldır;
                await _layoutEngine.RemoveWidgetAsync(widget);

                // Widget'ı kaldır;
                RemoveWidgetInternal(widgetId);

                // WidgetRemoved event'ini tetikle;
                OnWidgetRemoved(new WidgetEventArgs;
                {
                    WidgetId = widgetId,
                    WidgetType = widget.WidgetType,
                    Timestamp = DateTime.UtcNow;
                });

                UpdateStatistics();

                _logger.LogInformation($"Widget kaldırıldı: {widgetId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Widget kaldırma sırasında hata oluştu: {widgetId}");
                throw new HUDDesignerException($"Widget kaldırılamadı: {widgetId}", ex);
            }
        }

        /// <summary>
        /// Widget'ı günceller;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <param name="updateAction">Güncelleme aksiyonu</param>
        public async Task UpdateWidgetAsync(string widgetId, Action<HUDWidget> updateAction)
        {
            if (!_widgets.ContainsKey(widgetId))
                throw new HUDDesignerException($"Widget bulunamadı: {widgetId}");

            try
            {
                var widget = _widgets[widgetId];

                // Güncelleme yap;
                updateAction?.Invoke(widget);

                // Layout'u güncelle;
                await _layoutEngine.UpdateWidgetAsync(widget);

                // WidgetUpdated event'ini tetikle;
                OnWidgetUpdated(new WidgetUpdatedEventArgs;
                {
                    WidgetId = widgetId,
                    WidgetType = widget.WidgetType,
                    UpdateType = WidgetUpdateType.Properties,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Widget güncellendi: {widgetId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Widget güncelleme sırasında hata oluştu: {widgetId}");
                throw new HUDDesignerException($"Widget güncellenemedi: {widgetId}", ex);
            }
        }

        /// <summary>
        /// Widget'ın verilerini günceller;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <param name="data">Yeni veriler</param>
        public async Task UpdateWidgetDataAsync(string widgetId, object data)
        {
            if (!_widgets.ContainsKey(widgetId))
                throw new HUDDesignerException($"Widget bulunamadı: {widgetId}");

            try
            {
                var widget = _widgets[widgetId];

                // Verileri güncelle;
                if (widget is IDataWidget dataWidget)
                {
                    await dataWidget.UpdateDataAsync(data);

                    // WidgetUpdated event'ini tetikle;
                    OnWidgetUpdated(new WidgetUpdatedEventArgs;
                    {
                        WidgetId = widgetId,
                        WidgetType = widget.WidgetType,
                        UpdateType = WidgetUpdateType.Data,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                _logger.LogDebug($"Widget verileri güncellendi: {widgetId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Widget veri güncelleme sırasında hata oluştu: {widgetId}");
                throw new HUDDesignerException($"Widget verileri güncellenemedi: {widgetId}", ex);
            }
        }

        /// <summary>
        /// Widget'a animasyon uygular;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <param name="animationConfig">Animasyon konfigürasyonu</param>
        public async Task AnimateWidgetAsync(string widgetId, AnimationConfiguration animationConfig)
        {
            if (!_widgets.ContainsKey(widgetId))
                throw new HUDDesignerException($"Widget bulunamadı: {widgetId}");

            try
            {
                var widget = _widgets[widgetId];
                await _hudAnimator.AnimateWidgetAsync(widget, animationConfig);

                _logger.LogDebug($"Widget animasyonu başlatıldı: {widgetId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Widget animasyonu sırasında hata oluştu: {widgetId}");
                throw new HUDDesignerException($"Widget animasyonu uygulanamadı: {widgetId}", ex);
            }
        }

        /// <summary>
        /// Yeni bir katman ekler;
        /// </summary>
        /// <param name="layer">Katman</param>
        public void AddLayer(HUDLayer layer)
        {
            if (_layers.ContainsKey(layer.Id))
                throw new HUDDesignerException($"Katman zaten mevcut: {layer.Id}");

            try
            {
                AddLayerInternal(layer);
                _logger.LogInformation($"Katman eklendi: {layer.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Katman ekleme sırasında hata oluştu: {layer.Id}");
                throw new HUDDesignerException($"Katman eklenemedi: {layer.Id}", ex);
            }
        }

        /// <summary>
        /// Katmanı kaldırır;
        /// </summary>
        /// <param name="layerId">Katman ID</param>
        public void RemoveLayer(string layerId)
        {
            if (!_layers.ContainsKey(layerId))
                return;

            try
            {
                // Katmandaki tüm widget'ları kaldır;
                var widgetsInLayer = _widgets.Values;
                    .Where(w => w.LayerId == layerId)
                    .Select(w => w.Id)
                    .ToList();

                foreach (var widgetId in widgetsInLayer)
                {
                    RemoveWidgetInternal(widgetId);
                }

                // Katmanı kaldır;
                _layers.Remove(layerId);

                _logger.LogInformation($"Katman kaldırıldı: {layerId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Katman kaldırma sırasında hata oluştu: {layerId}");
                throw new HUDDesignerException($"Katman kaldırılamadı: {layerId}", ex);
            }
        }

        /// <summary>
        /// HUD'u render eder;
        /// </summary>
        public async Task RenderHUDAsync()
        {
            if (!_isInitialized)
                return;

            if (_isRendering)
                return;

            lock (_renderLock)
            {
                if (_isRendering)
                    return;

                _isRendering = true;
            }

            try
            {
                // Layout'u güncelle;
                await _layoutEngine.UpdateLayoutAsync();

                // Tüm widget'ları render et;
                foreach (var widget in _widgets.Values)
                {
                    if (widget.IsVisible && widget.NeedsRender)
                    {
                        await widget.RenderAsync();
                    }
                }

                // HUDRendered event'ini tetikle;
                OnHUDRendered(new HUDRenderEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    WidgetCount = _widgets.Count,
                    FrameRate = CalculateFrameRate()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUD render sırasında hata oluştu.");
            }
            finally
            {
                lock (_renderLock)
                {
                    _isRendering = false;
                }
            }
        }

        /// <summary>
        /// HUD temasını değiştirir;
        /// </summary>
        /// <param name="themeName">Tema adı</param>
        public async Task ChangeThemeAsync(string themeName)
        {
            try
            {
                await _themeManager.ChangeThemeAsync(themeName);

                // Widget'ları tema ile güncelle;
                foreach (var widget in _widgets.Values)
                {
                    widget.ApplyTheme(_themeManager.CurrentTheme);
                }

                _logger.LogInformation($"HUD teması değiştirildi: {themeName}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"HUD tema değiştirme sırasında hata oluştu: {themeName}");
                throw new HUDDesignerException($"HUD teması değiştirilemedi: {themeName}", ex);
            }
        }

        /// <summary>
        /// HUD'u dışa aktarır;
        /// </summary>
        /// <param name="exportType">Dışa aktarma türü</param>
        /// <returns>Dışa aktarılan veriler</returns>
        public async Task<HUDExportData> ExportHUDAsync(HUDExportType exportType)
        {
            try
            {
                _logger.LogInformation($"HUD dışa aktarılıyor. Tip: {exportType}");

                var exportData = new HUDExportData;
                {
                    ExportId = Guid.NewGuid().ToString(),
                    ExportType = exportType,
                    ExportTime = DateTime.UtcNow,
                    WidgetCount = _widgets.Count,
                    LayerCount = _layers.Count;
                };

                switch (exportType)
                {
                    case HUDExportType.XAML:
                        exportData.Data = await ExportToXAMLAsync();
                        break;
                    case HUDExportType.JSON:
                        exportData.Data = await ExportToJSONAsync();
                        break;
                    case HUDExportType.Binary:
                        exportData.Data = await ExportToBinaryAsync();
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen export tipi: {exportType}");
                }

                exportData.Success = true;
                exportData.Message = "HUD başarıyla dışa aktarıldı.";

                _logger.LogInformation($"HUD dışa aktarıldı. ID: {exportData.ExportId}");

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUD dışa aktarma sırasında hata oluştu.");
                throw new HUDDesignerException("HUD dışa aktarılamadı.", ex);
            }
        }

        /// <summary>
        /// HUD'u tasarım moduna alır;
        /// </summary>
        public void EnterDesignMode()
        {
            if (_isDesignMode)
                return;

            _isDesignMode = true;

            // Tüm widget'lara tasarım modu stilleri uygula;
            foreach (var widget in _widgets.Values)
            {
                widget.IsInDesignMode = true;
            }

            _logger.LogInformation("HUD tasarım moduna geçildi.");
        }

        /// <summary>
        /// HUD'u tasarım modundan çıkarır;
        /// </summary>
        public void ExitDesignMode()
        {
            if (!_isDesignMode)
                return;

            _isDesignMode = false;

            // Tasarım modu stillerini kaldır;
            foreach (var widget in _widgets.Values)
            {
                widget.IsInDesignMode = false;
            }

            _logger.LogInformation("HUD tasarım modundan çıkıldı.");
        }

        /// <summary>
        /// Widget'ı bulur;
        /// </summary>
        /// <param name="widgetId">Widget ID</param>
        /// <returns>Widget</returns>
        public HUDWidget FindWidget(string widgetId)
        {
            if (_widgets.TryGetValue(widgetId, out var widget))
            {
                return widget;
            }

            return null;
        }

        /// <summary>
        /// Widget'ları filtreler;
        /// </summary>
        /// <param name="predicate">Filtreleme koşulu</param>
        /// <returns>Filtrelenmiş widget'lar</returns>
        public IEnumerable<HUDWidget> FindWidgets(Func<HUDWidget, bool> predicate)
        {
            return _widgets.Values.Where(predicate);
        }

        /// <summary>
        /// HUD'u temizler;
        /// </summary>
        public void ClearHUD()
        {
            try
            {
                _logger.LogInformation("HUD temizleniyor...");

                // Tüm widget'ları kaldır;
                var widgetIds = _widgets.Keys.ToList();
                foreach (var widgetId in widgetIds)
                {
                    RemoveWidgetInternal(widgetId);
                }

                // Tüm katmanları temizle (varsayılanlar hariç)
                var customLayers = _layers.Keys;
                    .Where(k => !k.StartsWith("Default_"))
                    .ToList();

                foreach (var layerId in customLayers)
                {
                    _layers.Remove(layerId);
                }

                // Canvas'ı temizle;
                _hudCanvas.Children.Clear();

                UpdateStatistics();

                _logger.LogInformation("HUD başarıyla temizlendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUD temizleme sırasında hata oluştu.");
                throw new HUDDesignerException("HUD temizlenemedi.", ex);
            }
        }

        #endregion;

        #region Private Methods;

        private void AddWidgetInternal(HUDWidget widget, string layerId)
        {
            if (_widgets.ContainsKey(widget.Id))
                throw new HUDDesignerException($"Widget zaten mevcut: {widget.Id}");

            // Layer'ı ayarla;
            widget.LayerId = layerId;
            widget.ZIndex = _layers[layerId].ZIndex * 100 + _widgets.Count;

            // Canvas'a ekle;
            _hudCanvas.Children.Add(widget.VisualElement);
            Canvas.SetZIndex(widget.VisualElement, widget.ZIndex);

            // Widget'ı kaydet;
            _widgets.Add(widget.Id, widget);

            // Tema uygula;
            widget.ApplyTheme(_themeManager.CurrentTheme);
        }

        private void RemoveWidgetInternal(string widgetId)
        {
            if (!_widgets.TryGetValue(widgetId, out var widget))
                return;

            // Canvas'tan kaldır;
            _hudCanvas.Children.Remove(widget.VisualElement);

            // Widget'ı kaldır;
            _widgets.Remove(widgetId);

            // Widget'ı dispose et;
            widget.Dispose();
        }

        private void AddLayerInternal(HUDLayer layer)
        {
            _layers.Add(layer.Id, layer);
        }

        private void OnUpdateTimerTick(object sender, EventArgs e)
        {
            if (CurrentState != HUDState.Running)
                return;

            // HUD'u güncelle;
            UpdateHUDAsync().ContinueWith(task =>
            {
                if (task.IsFaulted)
                {
                    _logger.LogError(task.Exception, "HUD güncelleme sırasında hata oluştu.");
                }
            });
        }

        private async Task UpdateHUDAsync()
        {
            try
            {
                // Widget'ları güncelle;
                foreach (var widget in _widgets.Values)
                {
                    if (widget.IsVisible && widget.AutoUpdate)
                    {
                        await widget.UpdateAsync();
                    }
                }

                // Render et;
                await RenderHUDAsync();

                // İstatistikleri güncelle;
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "HUD güncelleme sırasında hata oluştu.");
            }
        }

        private void OnThemeChanged(object sender, ThemeChangedEventArgs e)
        {
            // Tüm widget'lara yeni temayı uygula;
            foreach (var widget in _widgets.Values)
            {
                widget.ApplyTheme(e.NewTheme);
            }

            _logger.LogInformation($"HUD teması uygulandı: {e.ThemeName}");
        }

        private double CalculateFrameRate()
        {
            // Basit FPS hesaplama;
            return 60.0; // Gerçek implementasyonda frame zamanları takip edilir;
        }

        private void UpdateStatistics()
        {
            Statistics = new HUDStatistics;
            {
                WidgetCount = _widgets.Count,
                LayerCount = _layers.Count,
                VisibleWidgetCount = _widgets.Values.Count(w => w.IsVisible),
                ActiveAnimations = _hudAnimator.ActiveAnimationCount,
                MemoryUsage = CalculateMemoryUsage(),
                LastUpdate = DateTime.UtcNow;
            };
        }

        private long CalculateMemoryUsage()
        {
            // Bellek kullanımını hesapla;
            long memory = 0;

            foreach (var widget in _widgets.Values)
            {
                memory += widget.MemoryUsage;
            }

            return memory;
        }

        private async Task<byte[]> ExportToXAMLAsync()
        {
            return await Task.Run(() =>
            {
                // XAML formatında dışa aktar;
                var xaml = System.Windows.Markup.XamlWriter.Save(_hudCanvas);
                return System.Text.Encoding.UTF8.GetBytes(xaml);
            });
        }

        private async Task<byte[]> ExportToJSONAsync()
        {
            return await Task.Run(() =>
            {
                var exportModel = new HUDExportModel;
                {
                    Widgets = _widgets.Values.Select(w => w.ToExportModel()).ToList(),
                    Layers = _layers.Values.ToList(),
                    Configuration = _configuration,
                    ExportTime = DateTime.UtcNow;
                };

                var json = System.Text.Json.JsonSerializer.Serialize(exportModel, new System.Text.Json.JsonSerializerOptions;
                {
                    WriteIndented = true;
                });

                return System.Text.Encoding.UTF8.GetBytes(json);
            });
        }

        private async Task<byte[]> ExportToBinaryAsync()
        {
            return await Task.Run(() =>
            {
                // Binary formatında dışa aktar;
                using var stream = new System.IO.MemoryStream();
                using var writer = new System.IO.BinaryWriter(stream);

                writer.Write(_widgets.Count);
                foreach (var widget in _widgets.Values)
                {
                    writer.Write(widget.Id);
                    writer.Write((int)widget.WidgetType);
                    // Diğer widget özellikleri...
                }

                return stream.ToArray();
            });
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnHUDInitialized(HUDInitializedEventArgs e)
        {
            HUDInitialized?.Invoke(this, e);
        }

        protected virtual void OnHUDStateChanged(HUDStateChangedEventArgs e)
        {
            HUDStateChanged?.Invoke(this, e);
        }

        protected virtual void OnWidgetAdded(WidgetEventArgs e)
        {
            WidgetAdded?.Invoke(this, e);
        }

        protected virtual void OnWidgetRemoved(WidgetEventArgs e)
        {
            WidgetRemoved?.Invoke(this, e);
        }

        protected virtual void OnWidgetUpdated(WidgetUpdatedEventArgs e)
        {
            WidgetUpdated?.Invoke(this, e);
        }

        protected virtual void OnHUDRendered(HUDRenderEventArgs e)
        {
            HUDRendered?.Invoke(this, e);
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
                    StopHUD();

                    _updateTimer.Stop();
                    _updateTimer.Tick -= OnUpdateTimerTick;

                    ClearHUD();

                    // Alt sistemleri dispose et;
                    (_hudAnimator as IDisposable)?.Dispose();
                    (_themeManager as IDisposable)?.Dispose();
                    (_layoutEngine as IDisposable)?.Dispose();
                    (_widgetFactory as IDisposable)?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~HUDDesigner()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IHUDDesigner : IDisposable
    {
        Task<HUDWidget> AddWidgetAsync(WidgetConfiguration widgetConfig);
        Task RemoveWidgetAsync(string widgetId);
        Task UpdateWidgetAsync(string widgetId, Action<HUDWidget> updateAction);
        Task UpdateWidgetDataAsync(string widgetId, object data);
        Task AnimateWidgetAsync(string widgetId, AnimationConfiguration animationConfig);
        Task RenderHUDAsync();
        Task ChangeThemeAsync(string themeName);
        Task<HUDExportData> ExportHUDAsync(HUDExportType exportType);

        void StartHUD();
        void StopHUD();
        void AddLayer(HUDLayer layer);
        void RemoveLayer(string layerId);
        void EnterDesignMode();
        void ExitDesignMode();
        void ClearHUD();

        HUDWidget FindWidget(string widgetId);
        IEnumerable<HUDWidget> FindWidgets(Func<HUDWidget, bool> predicate);

        IReadOnlyDictionary<string, HUDWidget> Widgets { get; }
        IReadOnlyDictionary<string, HUDLayer> Layers { get; }
        HUDState CurrentState { get; }
        HUDStatistics Statistics { get; }
        Canvas HUDElement { get; }

        event EventHandler<HUDInitializedEventArgs> HUDInitialized;
        event EventHandler<HUDStateChangedEventArgs> HUDStateChanged;
        event EventHandler<WidgetEventArgs> WidgetAdded;
        event EventHandler<WidgetEventArgs> WidgetRemoved;
        event EventHandler<WidgetUpdatedEventArgs> WidgetUpdated;
        event EventHandler<HUDRenderEventArgs> HUDRendered;
    }

    public interface IDataWidget;
    {
        Task UpdateDataAsync(object data);
    }

    public enum HUDState;
    {
        Uninitialized,
        Initializing,
        Ready,
        Starting,
        Running,
        Stopping,
        Stopped,
        Error;
    }

    public enum HUDExportType;
    {
        XAML,
        JSON,
        Binary;
    }

    public enum WidgetUpdateType;
    {
        Properties,
        Data,
        Animation,
        Visibility;
    }

    public class HUDInitializedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public int WidgetCount { get; set; }
        public int LayerCount { get; set; }
    }

    public class HUDStateChangedEventArgs : EventArgs;
    {
        public HUDState PreviousState { get; }
        public HUDState NewState { get; }

        public HUDStateChangedEventArgs(HUDState previousState, HUDState newState)
        {
            PreviousState = previousState;
            NewState = newState;
        }
    }

    public class WidgetEventArgs : EventArgs;
    {
        public string WidgetId { get; set; }
        public WidgetType WidgetType { get; set; }
        public string LayerId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class WidgetUpdatedEventArgs : WidgetEventArgs;
    {
        public WidgetUpdateType UpdateType { get; set; }
    }

    public class HUDRenderEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public int WidgetCount { get; set; }
        public double FrameRate { get; set; }
    }

    public class HUDExportData;
    {
        public string ExportId { get; set; }
        public HUDExportType ExportType { get; set; }
        public byte[] Data { get; set; }
        public DateTime ExportTime { get; set; }
        public int WidgetCount { get; set; }
        public int LayerCount { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class HUDExportModel;
    {
        public List<WidgetExportModel> Widgets { get; set; }
        public List<HUDLayer> Layers { get; set; }
        public HUDConfiguration Configuration { get; set; }
        public DateTime ExportTime { get; set; }
    }

    public class HUDDesignerException : Exception
    {
        public HUDDesignerException(string message) : base(message) { }
        public HUDDesignerException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
