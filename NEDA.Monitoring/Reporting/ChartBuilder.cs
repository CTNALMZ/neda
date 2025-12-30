// NEDA.Monitoring/Reporting/ChartBuilder.cs;

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Common.Utilities;
using NEDA.ExceptionHandling.ErrorCodes;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;
using SkiaSharp;
using Microcharts;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.Drawing;
using SkiaSharp;
using ScottPlot;
using System.Drawing.Drawing2D;

namespace NEDA.Monitoring.Reporting;
{
    /// <summary>
    /// Advanced chart building and visualization engine for data analytics and reporting;
    /// Supports multiple charting libraries with rich customization and real-time updates;
    /// </summary>
    public class ChartBuilder : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly ChartConfiguration _configuration;

        private readonly Dictionary<string, ChartTemplate> _chartTemplates;
        private readonly Dictionary<string, ChartTheme> _chartThemes;
        private readonly List<ChartRenderCache> _renderCache;

        private bool _isDisposed;
        private readonly object _templateLock = new object();
        private readonly object _themeLock = new object();

        /// <summary>
        /// Event triggered when chart is rendered;
        /// </summary>
        public event EventHandler<ChartRenderedEventArgs> OnChartRendered;

        /// <summary>
        /// Event triggered when chart template is created;
        /// </summary>
        public event EventHandler<ChartTemplateCreatedEventArgs> OnChartTemplateCreated;

        /// <summary>
        /// Event triggered when chart theme is applied;
        /// </summary>
        public event EventHandler<ChartThemeAppliedEventArgs> OnChartThemeApplied;

        /// <summary>
        /// Current chart builder status;
        /// </summary>
        public ChartBuilderStatus Status { get; private set; }

        /// <summary>
        /// Chart builder configuration;
        /// </summary>
        public ChartConfiguration Configuration => _configuration;

        /// <summary>
        /// Initialize chart builder with dependencies;
        /// </summary>
        public ChartBuilder(ILogger logger, IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));

            _configuration = LoadConfiguration();
            _chartTemplates = new Dictionary<string, ChartTemplate>();
            _chartThemes = new Dictionary<string, ChartTheme>();
            _renderCache = new List<ChartRenderCache>();

            InitializeBuiltInThemes();
            InitializeBuiltInTemplates();

            Status = ChartBuilderStatus.Ready;
            _logger.LogInformation("ChartBuilder initialized successfully", "ChartBuilder");
        }

        /// <summary>
        /// Create and render a chart with specified data and configuration;
        /// </summary>
        public async Task<ChartResult> CreateChartAsync(
            ChartData data,
            ChartOptions options,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var chartId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                Status = ChartBuilderStatus.Rendering;
                _logger.LogDebug($"Creating chart {chartId}: {options.ChartType}", "ChartBuilder");

                // Apply template if specified;
                if (!string.IsNullOrWhiteSpace(options.TemplateName))
                {
                    await ApplyTemplateAsync(options, cancellationToken);
                }

                // Apply theme if specified;
                if (!string.IsNullOrWhiteSpace(options.ThemeName))
                {
                    await ApplyThemeAsync(options, cancellationToken);
                }

                // Validate chart data;
                var validationResult = ValidateChartData(data, options);
                if (!validationResult.IsValid)
                {
                    throw new ChartDataException($"Chart data validation failed: {string.Join(", ", validationResult.Errors)}");
                }

                // Prepare chart based on type;
                ChartResult result;
                switch (options.ChartType)
                {
                    case ChartType.Line:
                        result = await CreateLineChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.Bar:
                        result = await CreateBarChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.Pie:
                        result = await CreatePieChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.Area:
                        result = await CreateAreaChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.Scatter:
                        result = await CreateScatterChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.HeatMap:
                        result = await CreateHeatMapChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.Gauge:
                        result = await CreateGaugeChartAsync(data, options, cancellationToken);
                        break;
                    case ChartType.Candlestick:
                        result = await CreateCandlestickChartAsync(data, options, cancellationToken);
                        break;
                    default:
                        throw new ChartTypeNotSupportedException($"Chart type '{options.ChartType}' is not supported");
                }

                result.ChartId = chartId;
                result.RenderTime = DateTime.UtcNow - startTime;

                // Cache the rendered chart if caching is enabled;
                if (_configuration.EnableCaching && result.ImageData != null)
                {
                    CacheChart(chartId, result, options);
                }

                // Trigger chart rendered event;
                OnChartRendered?.Invoke(this, new ChartRenderedEventArgs;
                {
                    ChartId = chartId,
                    ChartType = options.ChartType,
                    Result = result,
                    RenderTime = result.RenderTime,
                    Timestamp = DateTime.UtcNow;
                });

                Status = ChartBuilderStatus.Ready;
                _logger.LogInformation($"Chart {chartId} created successfully in {result.RenderTime.TotalMilliseconds:F0}ms",
                    "ChartBuilder");

                return result;
            }
            catch (OperationCanceledException)
            {
                Status = ChartBuilderStatus.Ready;
                _logger.LogInformation($"Chart creation {chartId} was cancelled", "ChartBuilder");
                throw;
            }
            catch (Exception ex)
            {
                Status = ChartBuilderStatus.Error;
                _logger.LogError($"Chart creation failed for {chartId}: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.ChartCreationFailed,
                    $"Chart creation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create a dashboard with multiple charts;
        /// </summary>
        public async Task<DashboardResult> CreateDashboardAsync(
            DashboardLayout layout,
            IEnumerable<DashboardChart> charts,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (layout == null)
                throw new ArgumentNullException(nameof(layout));

            if (charts == null)
                throw new ArgumentNullException(nameof(charts));

            var dashboardId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                Status = ChartBuilderStatus.RenderingDashboard;
                _logger.LogDebug($"Creating dashboard {dashboardId} with {charts.Count()} charts", "ChartBuilder");

                var dashboardResult = new DashboardResult;
                {
                    DashboardId = dashboardId,
                    Layout = layout,
                    StartTime = startTime;
                };

                // Create all charts in parallel;
                var chartTasks = new List<Task<ChartResult>>();
                var chartList = charts.ToList();

                foreach (var chart in chartList)
                {
                    chartTasks.Add(CreateChartAsync(chart.Data, chart.Options, cancellationToken));
                }

                var chartResults = await Task.WhenAll(chartTasks);

                // Arrange charts according to layout;
                await ArrangeChartsInDashboardAsync(dashboardResult, chartResults, layout, cancellationToken);

                dashboardResult.EndTime = DateTime.UtcNow;
                dashboardResult.RenderTime = dashboardResult.EndTime - dashboardResult.StartTime;
                dashboardResult.ChartCount = chartResults.Length;

                Status = ChartBuilderStatus.Ready;
                _logger.LogInformation($"Dashboard {dashboardId} created with {chartResults.Length} charts in {dashboardResult.RenderTime.TotalMilliseconds:F0}ms",
                    "ChartBuilder");

                return dashboardResult;
            }
            catch (OperationCanceledException)
            {
                Status = ChartBuilderStatus.Ready;
                _logger.LogInformation($"Dashboard creation {dashboardId} was cancelled", "ChartBuilder");
                throw;
            }
            catch (Exception ex)
            {
                Status = ChartBuilderStatus.Error;
                _logger.LogError($"Dashboard creation failed for {dashboardId}: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.DashboardCreationFailed,
                    $"Dashboard creation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Create a real-time updating chart;
        /// </summary>
        public async Task<RealTimeChart> CreateRealTimeChartAsync(
            RealTimeChartOptions options,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var chartId = Guid.NewGuid();

            try
            {
                var realTimeChart = new RealTimeChart;
                {
                    ChartId = chartId,
                    Options = options,
                    CreatedTime = DateTime.UtcNow,
                    IsActive = true;
                };

                // Initialize real-time data stream;
                realTimeChart.DataBuffer = new FixedSizeQueue<ChartDataPoint>(options.BufferSize);

                _logger.LogInformation($"Created real-time chart {chartId} with update interval {options.UpdateInterval.TotalMilliseconds}ms",
                    "ChartBuilder");

                return await Task.FromResult(realTimeChart);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Real-time chart creation failed for {chartId}: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.RealTimeChartFailed,
                    "Real-time chart creation failed", ex);
            }
        }

        /// <summary>
        /// Update a real-time chart with new data;
        /// </summary>
        public async Task<ChartResult> UpdateRealTimeChartAsync(
            RealTimeChart realTimeChart,
            ChartDataPoint dataPoint,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (realTimeChart == null)
                throw new ArgumentNullException(nameof(realTimeChart));

            if (!realTimeChart.IsActive)
                throw new InvalidOperationException("Real-time chart is not active");

            try
            {
                // Add data point to buffer;
                realTimeChart.DataBuffer.Enqueue(dataPoint);

                // Create chart data from buffer;
                var chartData = new ChartData;
                {
                    DataSets = new List<ChartDataSet>
                    {
                        new ChartDataSet;
                        {
                            Label = realTimeChart.Options.DataSeriesLabel,
                            DataPoints = realTimeChart.DataBuffer.ToList(),
                            Color = realTimeChart.Options.SeriesColor;
                        }
                    }
                };

                // Create updated chart;
                var options = new ChartOptions;
                {
                    ChartType = realTimeChart.Options.ChartType,
                    Width = realTimeChart.Options.Width,
                    Height = realTimeChart.Options.Height,
                    Title = realTimeChart.Options.Title,
                    ThemeName = realTimeChart.Options.ThemeName,
                    EnableAnimation = realTimeChart.Options.EnableAnimation;
                };

                var result = await CreateChartAsync(chartData, options, cancellationToken);
                result.ChartId = realTimeChart.ChartId;

                // Update real-time chart statistics;
                realTimeChart.UpdateCount++;
                realTimeChart.LastUpdateTime = DateTime.UtcNow;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Real-time chart update failed: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.RealTimeUpdateFailed,
                    "Real-time chart update failed", ex);
            }
        }

        /// <summary>
        /// Register a custom chart template;
        /// </summary>
        public void RegisterTemplate(ChartTemplate template)
        {
            ValidateNotDisposed();

            if (template == null)
                throw new ArgumentNullException(nameof(template));

            lock (_templateLock)
            {
                if (_chartTemplates.ContainsKey(template.Name))
                {
                    _chartTemplates[template.Name] = template;
                    _logger.LogInformation($"Updated chart template: {template.Name}", "ChartBuilder");
                }
                else;
                {
                    _chartTemplates.Add(template.Name, template);
                    _logger.LogInformation($"Registered chart template: {template.Name}", "ChartBuilder");
                }

                OnChartTemplateCreated?.Invoke(this, new ChartTemplateCreatedEventArgs;
                {
                    TemplateName = template.Name,
                    ChartType = template.ChartType,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Register a custom chart theme;
        /// </summary>
        public void RegisterTheme(ChartTheme theme)
        {
            ValidateNotDisposed();

            if (theme == null)
                throw new ArgumentNullException(nameof(theme));

            lock (_themeLock)
            {
                if (_chartThemes.ContainsKey(theme.Name))
                {
                    _chartThemes[theme.Name] = theme;
                    _logger.LogInformation($"Updated chart theme: {theme.Name}", "ChartBuilder");
                }
                else;
                {
                    _chartThemes.Add(theme.Name, theme);
                    _logger.LogInformation($"Registered chart theme: {theme.Name}", "ChartBuilder");
                }
            }
        }

        /// <summary>
        /// Get available chart templates;
        /// </summary>
        public List<ChartTemplate> GetTemplates()
        {
            ValidateNotDisposed();

            lock (_templateLock)
            {
                return _chartTemplates.Values.ToList();
            }
        }

        /// <summary>
        /// Get available chart themes;
        /// </summary>
        public List<ChartTheme> GetThemes()
        {
            ValidateNotDisposed();

            lock (_themeLock)
            {
                return _chartThemes.Values.ToList();
            }
        }

        /// <summary>
        /// Get cached chart by ID;
        /// </summary>
        public ChartResult GetCachedChart(Guid chartId)
        {
            ValidateNotDisposed();

            var cacheEntry = _renderCache.FirstOrDefault(c => c.ChartId == chartId);
            return cacheEntry?.Result;
        }

        /// <summary>
        /// Clear chart cache;
        /// </summary>
        public void ClearCache()
        {
            ValidateNotDisposed();

            _renderCache.Clear();
            _logger.LogInformation("Chart cache cleared", "ChartBuilder");
        }

        /// <summary>
        /// Export chart to various formats;
        /// </summary>
        public async Task<ExportResult> ExportChartAsync(
            ChartResult chartResult,
            ExportFormat format,
            string outputPath = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (chartResult == null)
                throw new ArgumentNullException(nameof(chartResult));

            if (chartResult.ImageData == null)
                throw new InvalidOperationException("Chart has no image data to export");

            try
            {
                var exportId = Guid.NewGuid();
                var exportStart = DateTime.UtcNow;

                _logger.LogDebug($"Exporting chart {chartResult.ChartId} to {format}", "ChartBuilder");

                byte[] exportData;
                string fileExtension;

                switch (format)
                {
                    case ExportFormat.PNG:
                        exportData = chartResult.ImageData;
                        fileExtension = ".png";
                        break;
                    case ExportFormat.JPEG:
                        exportData = await ConvertToJpegAsync(chartResult.ImageData, cancellationToken);
                        fileExtension = ".jpg";
                        break;
                    case ExportFormat.SVG:
                        exportData = await ConvertToSvgAsync(chartResult, cancellationToken);
                        fileExtension = ".svg";
                        break;
                    case ExportFormat.PDF:
                        exportData = await ConvertToPdfAsync(chartResult, cancellationToken);
                        fileExtension = ".pdf";
                        break;
                    case ExportFormat.BMP:
                        exportData = await ConvertToBmpAsync(chartResult.ImageData, cancellationToken);
                        fileExtension = ".bmp";
                        break;
                    default:
                        throw new ExportFormatNotSupportedException($"Export format '{format}' is not supported");
                }

                // Save to file if output path is provided;
                string finalPath = null;
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    finalPath = outputPath.EndsWith(fileExtension) ? outputPath : outputPath + fileExtension;
                    await File.WriteAllBytesAsync(finalPath, exportData, cancellationToken);
                }

                var result = new ExportResult;
                {
                    ExportId = exportId,
                    ChartId = chartResult.ChartId,
                    Format = format,
                    Data = exportData,
                    FilePath = finalPath,
                    SizeBytes = exportData.Length,
                    Success = true,
                    ExportTime = DateTime.UtcNow - exportStart;
                };

                _logger.LogInformation($"Chart exported: {chartResult.ChartId} to {format} ({exportData.Length} bytes)",
                    "ChartBuilder");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Chart export failed: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.ExportFailed, "Chart export failed", ex);
            }
        }

        /// <summary>
        /// Generate chart from template with data binding;
        /// </summary>
        public async Task<ChartResult> GenerateChartFromTemplateAsync(
            string templateName,
            Dictionary<string, object> dataBindings,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Template name cannot be empty", nameof(templateName));

            var template = GetTemplateByName(templateName);
            if (template == null)
                throw new TemplateNotFoundException($"Template '{templateName}' not found");

            try
            {
                // Bind data to template;
                var chartData = await BindDataToTemplateAsync(template, dataBindings, cancellationToken);

                // Create chart with template options;
                var options = template.Options.Clone();

                // Override options with any provided bindings;
                if (dataBindings.TryGetValue("ChartOptions", out var optionsObj) && optionsObj is ChartOptions overrideOptions)
                {
                    options = overrideOptions;
                }

                return await CreateChartAsync(chartData, options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Template-based chart generation failed: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.TemplateGenerationFailed,
                    "Template-based chart generation failed", ex);
            }
        }

        /// <summary>
        /// Create an interactive chart with hover effects and tooltips;
        /// </summary>
        public async Task<InteractiveChartResult> CreateInteractiveChartAsync(
            ChartData data,
            InteractiveChartOptions options,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (data == null)
                throw new ArgumentNullException(nameof(data));

            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var chartId = Guid.NewGuid();

            try
            {
                // First create the base chart;
                var baseOptions = new ChartOptions;
                {
                    ChartType = options.ChartType,
                    Width = options.Width,
                    Height = options.Height,
                    Title = options.Title,
                    ThemeName = options.ThemeName,
                    EnableAnimation = options.EnableAnimation;
                };

                var baseResult = await CreateChartAsync(data, baseOptions, cancellationToken);

                // Generate interactive elements;
                var interactiveElements = await GenerateInteractiveElementsAsync(data, options, cancellationToken);

                // Create tooltip data;
                var tooltips = await GenerateTooltipsAsync(data, options, cancellationToken);

                var result = new InteractiveChartResult;
                {
                    ChartId = chartId,
                    BaseChart = baseResult,
                    InteractiveElements = interactiveElements,
                    Tooltips = tooltips,
                    Options = options,
                    CreatedTime = DateTime.UtcNow;
                };

                _logger.LogInformation($"Created interactive chart {chartId} with {interactiveElements.Count} interactive elements",
                    "ChartBuilder");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Interactive chart creation failed: {ex.Message}", "ChartBuilder", ex);
                throw new ChartBuilderException(ChartErrorCodes.InteractiveChartFailed,
                    "Interactive chart creation failed", ex);
            }
        }

        #region Private Methods;

        private ChartConfiguration LoadConfiguration()
        {
            return new ChartConfiguration;
            {
                DefaultWidth = 800,
                DefaultHeight = 600,
                DefaultTheme = "DefaultDark",
                EnableCaching = true,
                CacheSize = 100,
                EnableAntialiasing = true,
                DefaultRenderEngine = RenderEngine.SkiaSharp,
                TempDirectory = Path.Combine(Path.GetTempPath(), "NEDA", "Charts"),
                MaxDataPointsPerChart = 10000,
                EnableDetailedLogging = false;
            };
        }

        private void InitializeBuiltInThemes()
        {
            // Dark Theme;
            RegisterTheme(new ChartTheme;
            {
                Name = "DefaultDark",
                DisplayName = "Default Dark",
                BackgroundColor = Color.FromArgb(30, 30, 30),
                TextColor = Color.FromArgb(220, 220, 220),
                GridColor = Color.FromArgb(70, 70, 70),
                AxisColor = Color.FromArgb(180, 180, 180),
                SeriesColors = new List<Color>
                {
                    Color.FromArgb(0, 150, 255),   // Blue;
                    Color.FromArgb(255, 100, 0),   // Orange;
                    Color.FromArgb(0, 200, 100),   // Green;
                    Color.FromArgb(255, 50, 150),  // Pink;
                    Color.FromArgb(200, 200, 0),   // Yellow;
                    Color.FromArgb(150, 0, 255)    // Purple;
                },
                IsDefault = true;
            });

            // Light Theme;
            RegisterTheme(new ChartTheme;
            {
                Name = "DefaultLight",
                DisplayName = "Default Light",
                BackgroundColor = Color.FromArgb(255, 255, 255),
                TextColor = Color.FromArgb(30, 30, 30),
                GridColor = Color.FromArgb(230, 230, 230),
                AxisColor = Color.FromArgb(100, 100, 100),
                SeriesColors = new List<Color>
                {
                    Color.FromArgb(0, 100, 200),   // Blue;
                    Color.FromArgb(255, 80, 0),    // Orange;
                    Color.FromArgb(0, 150, 80),    // Green;
                    Color.FromArgb(200, 40, 120),  // Pink;
                    Color.FromArgb(180, 180, 0),   // Yellow;
                    Color.FromArgb(120, 0, 200)    // Purple;
                }
            });

            // Corporate Theme;
            RegisterTheme(new ChartTheme;
            {
                Name = "Corporate",
                DisplayName = "Corporate",
                BackgroundColor = Color.FromArgb(245, 245, 245),
                TextColor = Color.FromArgb(50, 50, 50),
                GridColor = Color.FromArgb(220, 220, 220),
                AxisColor = Color.FromArgb(120, 120, 120),
                SeriesColors = new List<Color>
                {
                    Color.FromArgb(0, 120, 215),   // Corporate Blue;
                    Color.FromArgb(16, 124, 16),   // Corporate Green;
                    Color.FromArgb(216, 59, 1),    // Corporate Red;
                    Color.FromArgb(124, 114, 104), // Corporate Gray;
                    Color.FromArgb(0, 183, 195),   // Corporate Teal;
                    Color.FromArgb(181, 136, 0)    // Corporate Gold;
                }
            });
        }

        private void InitializeBuiltInTemplates()
        {
            // Line Chart Template;
            RegisterTemplate(new ChartTemplate;
            {
                Name = "LineChart_Default",
                DisplayName = "Default Line Chart",
                ChartType = ChartType.Line,
                Options = new ChartOptions;
                {
                    ChartType = ChartType.Line,
                    Width = 800,
                    Height = 400,
                    Title = "Line Chart",
                    ShowLegend = true,
                    ShowGrid = true,
                    EnableAnimation = true,
                    ThemeName = "DefaultDark"
                }
            });

            // Bar Chart Template;
            RegisterTemplate(new ChartTemplate;
            {
                Name = "BarChart_Default",
                DisplayName = "Default Bar Chart",
                ChartType = ChartType.Bar,
                Options = new ChartOptions;
                {
                    ChartType = ChartType.Bar,
                    Width = 800,
                    Height = 400,
                    Title = "Bar Chart",
                    ShowLegend = true,
                    ShowGrid = true,
                    EnableAnimation = true,
                    ThemeName = "DefaultDark"
                }
            });

            // Performance Dashboard Template;
            RegisterTemplate(new ChartTemplate;
            {
                Name = "PerformanceDashboard",
                DisplayName = "Performance Dashboard",
                ChartType = ChartType.Line,
                Options = new ChartOptions;
                {
                    ChartType = ChartType.Line,
                    Width = 1200,
                    Height = 600,
                    Title = "Performance Metrics",
                    ShowLegend = true,
                    ShowGrid = true,
                    EnableAnimation = false,
                    ThemeName = "DefaultDark",
                    CustomProperties = new Dictionary<string, object>
                    {
                        ["DashboardLayout"] = "Grid2x2",
                        ["RefreshInterval"] = 5000;
                    }
                }
            });
        }

        private async Task ApplyTemplateAsync(ChartOptions options, CancellationToken cancellationToken)
        {
            var template = GetTemplateByName(options.TemplateName);
            if (template == null)
                return;

            // Apply template settings to options;
            options.Width = template.Options.Width;
            options.Height = template.Options.Height;
            options.ThemeName = template.Options.ThemeName;
            options.ShowLegend = template.Options.ShowLegend;
            options.ShowGrid = template.Options.ShowGrid;
            options.EnableAnimation = template.Options.EnableAnimation;

            // Merge custom properties;
            if (template.Options.CustomProperties != null)
            {
                foreach (var prop in template.Options.CustomProperties)
                {
                    options.CustomProperties[prop.Key] = prop.Value;
                }
            }

            await Task.CompletedTask;
        }

        private async Task ApplyThemeAsync(ChartOptions options, CancellationToken cancellationToken)
        {
            var theme = GetThemeByName(options.ThemeName);
            if (theme == null)
                return;

            // Apply theme settings;
            options.BackgroundColor = theme.BackgroundColor;
            options.TextColor = theme.TextColor;
            options.GridColor = theme.GridColor;
            options.AxisColor = theme.AxisColor;

            // Set series colors if not explicitly set;
            if (options.SeriesColors == null || !options.SeriesColors.Any())
            {
                options.SeriesColors = theme.SeriesColors;
            }

            OnChartThemeApplied?.Invoke(this, new ChartThemeAppliedEventArgs;
            {
                ThemeName = theme.Name,
                ChartType = options.ChartType,
                Timestamp = DateTime.UtcNow;
            });

            await Task.CompletedTask;
        }

        private ChartTemplate GetTemplateByName(string name)
        {
            lock (_templateLock)
            {
                return _chartTemplates.TryGetValue(name, out var template) ? template : null;
            }
        }

        private ChartTheme GetThemeByName(string name)
        {
            lock (_themeLock)
            {
                return _chartThemes.TryGetValue(name, out var theme) ? theme : null;
            }
        }

        private ChartDataValidationResult ValidateChartData(ChartData data, ChartOptions options)
        {
            var result = new ChartDataValidationResult;
            {
                IsValid = true;
            };

            if (data == null || data.DataSets == null || !data.DataSets.Any())
            {
                result.IsValid = false;
                result.Errors.Add("Chart data is empty");
                return result;
            }

            // Check data set consistency;
            foreach (var dataSet in data.DataSets)
            {
                if (dataSet.DataPoints == null || !dataSet.DataPoints.Any())
                {
                    result.IsValid = false;
                    result.Errors.Add($"Data set '{dataSet.Label}' has no data points");
                }

                // Check for maximum data points;
                if (dataSet.DataPoints.Count > _configuration.MaxDataPointsPerChart)
                {
                    result.Warnings.Add($"Data set '{dataSet.Label}' has {dataSet.DataPoints.Count} points, which may affect performance");
                }
            }

            // Validate based on chart type;
            switch (options.ChartType)
            {
                case ChartType.Pie:
                    if (data.DataSets.Count > 1)
                    {
                        result.Warnings.Add("Pie charts typically display a single data set");
                    }
                    break;
                case ChartType.Gauge:
                    if (data.DataSets.Count != 1 || data.DataSets[0].DataPoints.Count != 1)
                    {
                        result.IsValid = false;
                        result.Errors.Add("Gauge charts require exactly one data point");
                    }
                    break;
            }

            return result;
        }

        private async Task<ChartResult> CreateLineChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // Add data sets;
            int colorIndex = 0;
            foreach (var dataSet in data.DataSets)
            {
                var xs = dataSet.DataPoints.Select(d => d.X).ToArray();
                var ys = dataSet.DataPoints.Select(d => d.Y).ToArray();

                var color = options.SeriesColors != null && options.SeriesColors.Count > colorIndex;
                    ? options.SeriesColors[colorIndex]
                    : GetDefaultColor(colorIndex);

                chart.Plot.AddScatterLines(xs, ys, color: color.ToSKColor(),
                    label: dataSet.Label, lineWidth: 2);

                colorIndex++;
            }

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);
            chart.Plot.XLabel(options.XAxisLabel);
            chart.Plot.YLabel(options.YAxisLabel);

            if (options.ShowLegend)
            {
                chart.Plot.Legend();
            }

            if (options.ShowGrid)
            {
                chart.Plot.Grid(enable: true);
            }

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = data.DataSets.Sum(ds => ds.DataPoints?.Count ?? 0)
            };
        }

        private async Task<ChartResult> CreateBarChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // For bar charts, we need categorical data;
            // Assume first data set provides the categories;
            var positions = new List<double>();
            var values = new List<double>();
            var labels = new List<string>();

            if (data.DataSets.Any())
            {
                var firstDataSet = data.DataSets.First();
                for (int i = 0; i < firstDataSet.DataPoints.Count; i++)
                {
                    positions.Add(i);
                    values.Add(firstDataSet.DataPoints[i].Y);
                    labels.Add(firstDataSet.DataPoints[i].Label ?? $"Item {i + 1}");
                }
            }

            var bars = chart.Plot.AddBar(values.ToArray(), positions.ToArray());
            bars.FillColor = options.SeriesColors != null && options.SeriesColors.Any()
                ? options.SeriesColors[0].ToSKColor()
                : SKColors.SteelBlue;

            // Set X-axis labels;
            chart.Plot.XTicks(positions.ToArray(), labels.ToArray());

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);
            chart.Plot.XLabel(options.XAxisLabel);
            chart.Plot.YLabel(options.YAxisLabel);

            if (options.ShowGrid)
            {
                chart.Plot.Grid(enable: true);
            }

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = data.DataSets.Sum(ds => ds.DataPoints?.Count ?? 0)
            };
        }

        private async Task<ChartResult> CreatePieChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // Prepare pie chart data;
            var values = new List<double>();
            var labels = new List<string>();
            var colors = new List<SKColor>();

            if (data.DataSets.Any())
            {
                var dataSet = data.DataSets.First(); // Pie charts typically use one data set;
                for (int i = 0; i < dataSet.DataPoints.Count; i++)
                {
                    values.Add(dataSet.DataPoints[i].Y);
                    labels.Add(dataSet.DataPoints[i].Label ?? $"Slice {i + 1}");

                    var color = options.SeriesColors != null && options.SeriesColors.Count > i;
                        ? options.SeriesColors[i]
                        : GetDefaultColor(i);
                    colors.Add(color.ToSKColor());
                }
            }

            var pie = chart.Plot.AddPie(values.ToArray());
            pie.SliceLabels = labels.ToArray();
            pie.Colors = colors.ToArray();
            pie.ShowLabels = true;
            pie.Explode = true; // Slightly separate slices;

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);
            chart.Plot.Legend();

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = values.Count;
            };
        }

        private async Task<ChartResult> CreateAreaChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            // Similar to line chart but with filled areas;
            return await CreateLineChartAsync(data, options, cancellationToken);
        }

        private async Task<ChartResult> CreateScatterChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // Add data sets;
            int colorIndex = 0;
            foreach (var dataSet in data.DataSets)
            {
                var xs = dataSet.DataPoints.Select(d => d.X).ToArray();
                var ys = dataSet.DataPoints.Select(d => d.Y).ToArray();

                var color = options.SeriesColors != null && options.SeriesColors.Count > colorIndex;
                    ? options.SeriesColors[colorIndex]
                    : GetDefaultColor(colorIndex);

                chart.Plot.AddScatter(xs, ys, color: color.ToSKColor(),
                    label: dataSet.Label, markerSize: 5);

                colorIndex++;
            }

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);
            chart.Plot.XLabel(options.XAxisLabel);
            chart.Plot.YLabel(options.YAxisLabel);

            if (options.ShowLegend)
            {
                chart.Plot.Legend();
            }

            if (options.ShowGrid)
            {
                chart.Plot.Grid(enable: true);
            }

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = data.DataSets.Sum(ds => ds.DataPoints?.Count ?? 0)
            };
        }

        private async Task<ChartResult> CreateHeatMapChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // Heat maps require 2D data;
            // For simplicity, assume first data set provides a grid of points;
            if (data.DataSets.Any())
            {
                var dataSet = data.DataSets.First();

                // Organize data into 2D array;
                var xValues = dataSet.DataPoints.Select(d => d.X).Distinct().OrderBy(x => x).ToArray();
                var yValues = dataSet.DataPoints.Select(d => d.Y).Distinct().OrderBy(y => y).ToArray();

                var intensities = new double[yValues.Length, xValues.Length];

                foreach (var point in dataSet.DataPoints)
                {
                    var xIndex = Array.IndexOf(xValues, point.X);
                    var yIndex = Array.IndexOf(yValues, point.Y);

                    if (xIndex >= 0 && yIndex >= 0)
                    {
                        intensities[yIndex, xIndex] = point.Z; // Use Z value for intensity;
                    }
                }

                var hm = chart.Plot.AddHeatmap(intensities);
                hm.Colormap = ScottPlot.Drawing.Colormap.Viridis;
            }

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);
            chart.Plot.XLabel(options.XAxisLabel);
            chart.Plot.YLabel(options.YAxisLabel);

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = data.DataSets.Sum(ds => ds.DataPoints?.Count ?? 0)
            };
        }

        private async Task<ChartResult> CreateGaugeChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // Gauge chart implementation;
            if (data.DataSets.Any() && data.DataSets.First().DataPoints.Any())
            {
                var value = data.DataSets.First().DataPoints.First().Y;
                var maxValue = options.CustomProperties.TryGetValue("MaxValue", out var max)
                    ? Convert.ToDouble(max)
                    : 100;

                // Draw gauge;
                chart.Plot.AddCircle(0, 0, 1, color: SKColors.LightGray);
                chart.Plot.AddCircle(0, 0, 0.8, color: SKColors.White);

                // Draw value indicator;
                var angle = (value / maxValue) * Math.PI;
                var x = Math.Sin(angle) * 0.7;
                var y = Math.Cos(angle) * 0.7;

                chart.Plot.AddLine(0, 0, x, y, color: SKColors.Red, lineWidth: 3);

                // Add value text;
                chart.Plot.AddText($"{value:F1}", 0, -0.3, size: 20,
                    color: options.TextColor.ToSKColor(), bold: true);
            }

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = 1;
            };
        }

        private async Task<ChartResult> CreateCandlestickChartAsync(ChartData data, ChartOptions options,
            CancellationToken cancellationToken)
        {
            var chart = new Plot(options.Width, options.Height);

            // Apply theme colors;
            chart.Style(options.BackgroundColor, options.GridColor, options.TextColor, options.AxisColor);

            // Candlestick chart requires OHLC data;
            // Assume each data point has Open, High, Low, Close values in custom properties;
            if (data.DataSets.Any())
            {
                var dataSet = data.DataSets.First();
                var dates = dataSet.DataPoints.Select(d => d.X).ToArray();
                var opens = new List<double>();
                var highs = new List<double>();
                var lows = new List<double>();
                var closes = new List<double>();

                foreach (var point in dataSet.DataPoints)
                {
                    opens.Add(point.CustomProperties.TryGetValue("Open", out var open) ? Convert.ToDouble(open) : point.Y);
                    highs.Add(point.CustomProperties.TryGetValue("High", out var high) ? Convert.ToDouble(high) : point.Y);
                    lows.Add(point.CustomProperties.TryGetValue("Low", out var low) ? Convert.ToDouble(low) : point.Y);
                    closes.Add(point.CustomProperties.TryGetValue("Close", out var close) ? Convert.ToDouble(close) : point.Y);
                }

                var candles = chart.Plot.AddCandlesticks(
                    dates.Select(d => (d, opens.ToArray(), highs.ToArray(), lows.ToArray(), closes.ToArray())).ToArray());
            }

            // Configure chart;
            chart.Plot.Title(options.Title, size: 16);
            chart.Plot.XLabel(options.XAxisLabel);
            chart.Plot.YLabel(options.YAxisLabel);

            if (options.ShowGrid)
            {
                chart.Plot.Grid(enable: true);
            }

            // Render to image;
            var imageBytes = chart.GetImageBytes();

            return new ChartResult;
            {
                ImageData = imageBytes,
                Format = ImageFormat.Png,
                Width = options.Width,
                Height = options.Height,
                DataPointCount = data.DataSets.Sum(ds => ds.DataPoints?.Count ?? 0)
            };
        }

        private async Task ArrangeChartsInDashboardAsync(
            DashboardResult dashboardResult,
            ChartResult[] chartResults,
            DashboardLayout layout,
            CancellationToken cancellationToken)
        {
            // Create a composite image of all charts arranged according to layout;
            int totalWidth = layout.Columns * layout.CellWidth + (layout.Columns - 1) * layout.CellSpacing;
            int totalHeight = layout.Rows * layout.CellHeight + (layout.Rows - 1) * layout.CellSpacing;

            using (var bitmap = new Bitmap(totalWidth, totalHeight))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(layout.BackgroundColor);

                // Arrange charts in grid;
                for (int row = 0; row < layout.Rows; row++)
                {
                    for (int col = 0; col < layout.Columns; col++)
                    {
                        int chartIndex = row * layout.Columns + col;
                        if (chartIndex < chartResults.Length && chartResults[chartIndex].ImageData != null)
                        {
                            int x = col * (layout.CellWidth + layout.CellSpacing);
                            int y = row * (layout.CellHeight + layout.CellSpacing);

                            using (var chartStream = new MemoryStream(chartResults[chartIndex].ImageData))
                            using (var chartImage = Image.FromStream(chartStream))
                            {
                                graphics.DrawImage(chartImage, x, y, layout.CellWidth, layout.CellHeight);
                            }
                        }
                    }
                }

                // Add dashboard title;
                if (!string.IsNullOrWhiteSpace(layout.Title))
                {
                    using (var font = new Font("Arial", 20, FontStyle.Bold))
                    using (var brush = new SolidBrush(layout.TitleColor))
                    {
                        var titleSize = graphics.MeasureString(layout.Title, font);
                        graphics.DrawString(layout.Title, font, brush,
                            (totalWidth - titleSize.Width) / 2, 20);
                    }
                }

                // Save to memory stream;
                using (var memoryStream = new MemoryStream())
                {
                    bitmap.Save(memoryStream, ImageFormat.Png);
                    dashboardResult.ImageData = memoryStream.ToArray();
                }
            }

            dashboardResult.Width = totalWidth;
            dashboardResult.Height = totalHeight;

            await Task.CompletedTask;
        }

        private async Task<byte[]> ConvertToJpegAsync(byte[] pngData, CancellationToken cancellationToken)
        {
            using (var inputStream = new MemoryStream(pngData))
            using (var image = Image.FromStream(inputStream))
            using (var outputStream = new MemoryStream())
            {
                image.Save(outputStream, ImageFormat.Jpeg);
                return outputStream.ToArray();
            }
        }

        private async Task<byte[]> ConvertToSvgAsync(ChartResult chartResult, CancellationToken cancellationToken)
        {
            // Simple SVG generation - in production would use a proper SVG library;
            var svg = $@"<svg width='{chartResult.Width}' height='{chartResult.Height}' xmlns='http://www.w3.org/2000/svg'>
                <rect width='100%' height='100%' fill='#1e1e1e'/>
                <text x='50%' y='30' text-anchor='middle' fill='#ffffff' font-size='16'>{chartResult.Title}</text>
                <text x='50%' y='{chartResult.Height - 20}' text-anchor='middle' fill='#888888' font-size='12'>Generated by NEDA ChartBuilder</text>
            </svg>";

            return System.Text.Encoding.UTF8.GetBytes(svg);
        }

        private async Task<byte[]> ConvertToPdfAsync(ChartResult chartResult, CancellationToken cancellationToken)
        {
            // Simple PDF generation - in production would use iTextSharp or similar;
            var pdf = $@"%PDF-1.4;
1 0 obj;
<< /Type /Catalog /Pages 2 0 R >>
endobj;
2 0 obj;
<< /Type /Pages /Kids [3 0 R] /Count 1 >>
endobj;
3 0 obj;
<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {chartResult.Width} {chartResult.Height}] /Contents 4 0 R >>
endobj;
4 0 obj;
<< /Length 44 >>
stream;
BT /F1 12 Tf 100 700 Td (Chart: {chartResult.Title}) Tj ET;
endstream;
endobj;
xref;
0 5;
0000000000 65535 f; 
0000000010 00000 n; 
0000000053 00000 n; 
0000000102 00000 n; 
0000000178 00000 n; 
trailer;
<< /Size 5 /Root 1 0 R >>
startxref;
258;
%%EOF";

            return System.Text.Encoding.UTF8.GetBytes(pdf);
        }

        private async Task<byte[]> ConvertToBmpAsync(byte[] imageData, CancellationToken cancellationToken)
        {
            using (var inputStream = new MemoryStream(imageData))
            using (var image = Image.FromStream(inputStream))
            using (var outputStream = new MemoryStream())
            {
                image.Save(outputStream, ImageFormat.Bmp);
                return outputStream.ToArray();
            }
        }

        private async Task<ChartData> BindDataToTemplateAsync(
            ChartTemplate template,
            Dictionary<string, object> dataBindings,
            CancellationToken cancellationToken)
        {
            // Extract data from bindings;
            var chartData = new ChartData();

            if (dataBindings.TryGetValue("DataSets", out var dataSetsObj) && dataSetsObj is List<ChartDataSet> dataSets)
            {
                chartData.DataSets = dataSets;
            }
            else;
            {
                // Create default data set from numeric bindings;
                chartData.DataSets = new List<ChartDataSet>();

                foreach (var binding in dataBindings)
                {
                    if (binding.Value is IEnumerable<double> numericValues)
                    {
                        var dataSet = new ChartDataSet;
                        {
                            Label = binding.Key,
                            DataPoints = numericValues.Select((v, i) => new ChartDataPoint;
                            {
                                X = i,
                                Y = v,
                                Label = $"{binding.Key}_{i}"
                            }).ToList()
                        };

                        chartData.DataSets.Add(dataSet);
                    }
                }
            }

            return await Task.FromResult(chartData);
        }

        private async Task<List<InteractiveElement>> GenerateInteractiveElementsAsync(
            ChartData data,
            InteractiveChartOptions options,
            CancellationToken cancellationToken)
        {
            var elements = new List<InteractiveElement>();

            // Generate interactive elements for each data point;
            foreach (var dataSet in data.DataSets)
            {
                for (int i = 0; i < dataSet.DataPoints.Count; i++)
                {
                    var point = dataSet.DataPoints[i];

                    var element = new InteractiveElement;
                    {
                        ElementId = Guid.NewGuid(),
                        DataPointIndex = i,
                        DataSetIndex = data.DataSets.IndexOf(dataSet),
                        Bounds = new RectangleF(
                            (float)point.X - 10,
                            (float)point.Y - 10,
                            20, 20),
                        TooltipContent = GenerateTooltipContent(point, dataSet.Label),
                        IsVisible = true;
                    };

                    elements.Add(element);
                }
            }

            return await Task.FromResult(elements);
        }

        private async Task<Dictionary<Guid, string>> GenerateTooltipsAsync(
            ChartData data,
            InteractiveChartOptions options,
            CancellationToken cancellationToken)
        {
            var tooltips = new Dictionary<Guid, string>();

            // Generate tooltips for each data set;
            foreach (var dataSet in data.DataSets)
            {
                for (int i = 0; i < dataSet.DataPoints.Count; i++)
                {
                    var point = dataSet.DataPoints[i];
                    var elementId = Guid.NewGuid(); // In reality, this would match the interactive element;

                    var tooltip = $@"
                        <div class='chart-tooltip'>
                            <div class='tooltip-title'>{dataSet.Label}</div>
                            <div class='tooltip-content'>
                                <div>X: {point.X:F2}</div>
                                <div>Y: {point.Y:F2}</div>
                                <div>Value: {point.Value:F2}</div>
                            </div>
                        </div>";

                    tooltips[elementId] = tooltip;
                }
            }

            return await Task.FromResult(tooltips);
        }

        private string GenerateTooltipContent(ChartDataPoint point, string seriesLabel)
        {
            return $@"
                <div style='background: #333; color: #fff; padding: 8px; border-radius: 4px; font-family: Arial;'>
                    <div style='font-weight: bold; margin-bottom: 4px;'>{seriesLabel}</div>
                    <div>X: {point.X:F2}</div>
                    <div>Y: {point.Y:F2}</div>
                    <div>Value: {point.Value:F2}</div>
                </div>";
        }

        private void CacheChart(Guid chartId, ChartResult result, ChartOptions options)
        {
            var cacheEntry = new ChartRenderCache;
            {
                ChartId = chartId,
                Result = result,
                Options = options,
                CachedTime = DateTime.UtcNow;
            };

            _renderCache.Add(cacheEntry);

            // Maintain cache size;
            if (_renderCache.Count > _configuration.CacheSize)
            {
                var oldest = _renderCache.OrderBy(c => c.CachedTime).First();
                _renderCache.Remove(oldest);
            }
        }

        private Color GetDefaultColor(int index)
        {
            var colors = new[]
            {
                Color.FromArgb(0, 150, 255),   // Blue;
                Color.FromArgb(255, 100, 0),   // Orange;
                Color.FromArgb(0, 200, 100),   // Green;
                Color.FromArgb(255, 50, 150),  // Pink;
                Color.FromArgb(200, 200, 0),   // Yellow;
                Color.FromArgb(150, 0, 255)    // Purple;
            };

            return colors[index % colors.Length];
        }

        private void ValidateNotDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ChartBuilder));
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    ClearCache();
                    Status = ChartBuilderStatus.Disposed;
                    _logger.LogInformation("ChartBuilder disposed", "ChartBuilder");
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Chart builder status;
    /// </summary>
    public enum ChartBuilderStatus;
    {
        Ready,
        Rendering,
        RenderingDashboard,
        Error,
        Disposed;
    }

    /// <summary>
    /// Chart type enumeration;
    /// </summary>
    public enum ChartType;
    {
        Line,
        Bar,
        Pie,
        Area,
        Scatter,
        HeatMap,
        Gauge,
        Candlestick,
        Radar,
        Bubble,
        Funnel,
        TreeMap,
        Sunburst;
    }

    /// <summary>
    /// Chart rendering engine;
    /// </summary>
    public enum RenderEngine;
    {
        SkiaSharp,
        SystemDrawing,
        ScottPlot,
        LiveCharts,
        Custom;
    }

    /// <summary>
    /// Export format for charts;
    /// </summary>
    public enum ExportFormat;
    {
        PNG,
        JPEG,
        SVG,
        PDF,
        BMP;
    }

    /// <summary>
    /// Chart builder configuration;
    /// </summary>
    public class ChartConfiguration;
    {
        public int DefaultWidth { get; set; }
        public int DefaultHeight { get; set; }
        public string DefaultTheme { get; set; }
        public bool EnableCaching { get; set; }
        public int CacheSize { get; set; }
        public bool EnableAntialiasing { get; set; }
        public RenderEngine DefaultRenderEngine { get; set; }
        public string TempDirectory { get; set; }
        public int MaxDataPointsPerChart { get; set; }
        public bool EnableDetailedLogging { get; set; }
    }

    /// <summary>
    /// Chart data structure;
    /// </summary>
    public class ChartData;
    {
        public List<ChartDataSet> DataSets { get; set; } = new List<ChartDataSet>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Chart data set;
    /// </summary>
    public class ChartDataSet;
    {
        public string Label { get; set; }
        public List<ChartDataPoint> DataPoints { get; set; } = new List<ChartDataPoint>();
        public Color Color { get; set; }
        public float LineWidth { get; set; } = 2f;
        public bool ShowPoints { get; set; } = true;
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Chart data point;
    /// </summary>
    public class ChartDataPoint;
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public double Value { get; set; }
        public string Label { get; set; }
        public Color Color { get; set; }
        public Dictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Chart options;
    /// </summary>
    public class ChartOptions;
    {
        public ChartType ChartType { get; set; }
        public int Width { get; set; } = 800;
        public int Height { get; set; } = 600;
        public string Title { get; set; }
        public string XAxisLabel { get; set; }
        public string YAxisLabel { get; set; }
        public string TemplateName { get; set; }
        public string ThemeName { get; set; }
        public Color BackgroundColor { get; set; }
        public Color TextColor { get; set; }
        public Color GridColor { get; set; }
        public Color AxisColor { get; set; }
        public List<Color> SeriesColors { get; set; }
        public bool ShowLegend { get; set; } = true;
        public bool ShowGrid { get; set; } = true;
        public bool EnableAnimation { get; set; } = true;
        public Dictionary<string, object> CustomProperties { get; set; } = new Dictionary<string, object>();

        public ChartOptions Clone()
        {
            return new ChartOptions;
            {
                ChartType = ChartType,
                Width = Width,
                Height = Height,
                Title = Title,
                XAxisLabel = XAxisLabel,
                YAxisLabel = YAxisLabel,
                TemplateName = TemplateName,
                ThemeName = ThemeName,
                BackgroundColor = BackgroundColor,
                TextColor = TextColor,
                GridColor = GridColor,
                AxisColor = AxisColor,
                SeriesColors = SeriesColors?.ToList(),
                ShowLegend = ShowLegend,
                ShowGrid = ShowGrid,
                EnableAnimation = EnableAnimation,
                CustomProperties = new Dictionary<string, object>(CustomProperties)
            };
        }
    }

    /// <summary>
    /// Chart result;
    /// </summary>
    public class ChartResult;
    {
        public Guid ChartId { get; set; }
        public byte[] ImageData { get; set; }
        public ImageFormat Format { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Title { get; set; }
        public ChartType ChartType { get; set; }
        public int DataPointCount { get; set; }
        public TimeSpan RenderTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Chart template;
    /// </summary>
    public class ChartTemplate;
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public ChartType ChartType { get; set; }
        public ChartOptions Options { get; set; }
        public Dictionary<string, object> DefaultDataBindings { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
        public DateTime LastModifiedTime { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Chart theme;
    /// </summary>
    public class ChartTheme;
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public Color BackgroundColor { get; set; }
        public Color TextColor { get; set; }
        public Color GridColor { get; set; }
        public Color AxisColor { get; set; }
        public List<Color> SeriesColors { get; set; } = new List<Color>();
        public bool IsDefault { get; set; }
        public Dictionary<string, object> ThemeProperties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dashboard layout;
    /// </summary>
    public class DashboardLayout;
    {
        public int Rows { get; set; } = 2;
        public int Columns { get; set; } = 2;
        public int CellWidth { get; set; } = 400;
        public int CellHeight { get; set; } = 300;
        public int CellSpacing { get; set; } = 20;
        public string Title { get; set; }
        public Color BackgroundColor { get; set; } = Color.FromArgb(240, 240, 240);
        public Color TitleColor { get; set; } = Color.FromArgb(50, 50, 50);
        public Dictionary<string, object> LayoutProperties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dashboard chart definition;
    /// </summary>
    public class DashboardChart;
    {
        public string ChartId { get; set; }
        public ChartData Data { get; set; }
        public ChartOptions Options { get; set; }
        public int Row { get; set; }
        public int Column { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Dashboard result;
    /// </summary>
    public class DashboardResult;
    {
        public Guid DashboardId { get; set; }
        public byte[] ImageData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public DashboardLayout Layout { get; set; }
        public int ChartCount { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan RenderTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Real-time chart options;
    /// </summary>
    public class RealTimeChartOptions;
    {
        public ChartType ChartType { get; set; } = ChartType.Line;
        public int Width { get; set; } = 800;
        public int Height { get; set; } = 400;
        public string Title { get; set; }
        public string DataSeriesLabel { get; set; }
        public Color SeriesColor { get; set; } = Color.FromArgb(0, 150, 255);
        public string ThemeName { get; set; }
        public TimeSpan UpdateInterval { get; set; } = TimeSpan.FromSeconds(1);
        public int BufferSize { get; set; } = 100;
        public bool EnableAnimation { get; set; } = true;
        public Dictionary<string, object> ChartProperties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Real-time chart;
    /// </summary>
    public class RealTimeChart;
    {
        public Guid ChartId { get; set; }
        public RealTimeChartOptions Options { get; set; }
        public FixedSizeQueue<ChartDataPoint> DataBuffer { get; set; }
        public int UpdateCount { get; set; }
        public DateTime CreatedTime { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public bool IsActive { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Interactive chart options;
    /// </summary>
    public class InteractiveChartOptions;
    {
        public ChartType ChartType { get; set; }
        public int Width { get; set; } = 800;
        public int Height { get; set; } = 600;
        public string Title { get; set; }
        public string ThemeName { get; set; }
        public bool EnableAnimation { get; set; } = true;
        public bool EnableTooltips { get; set; } = true;
        public bool EnableZoom { get; set; } = true;
        public bool EnablePan { get; set; } = true;
        public bool EnableHoverEffects { get; set; } = true;
        public Dictionary<string, object> InteractiveProperties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Interactive chart result;
    /// </summary>
    public class InteractiveChartResult;
    {
        public Guid ChartId { get; set; }
        public ChartResult BaseChart { get; set; }
        public List<InteractiveElement> InteractiveElements { get; set; } = new List<InteractiveElement>();
        public Dictionary<Guid, string> Tooltips { get; set; } = new Dictionary<Guid, string>();
        public InteractiveChartOptions Options { get; set; }
        public DateTime CreatedTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Interactive element;
    /// </summary>
    public class InteractiveElement;
    {
        public Guid ElementId { get; set; }
        public int DataSetIndex { get; set; }
        public int DataPointIndex { get; set; }
        public RectangleF Bounds { get; set; }
        public string TooltipContent { get; set; }
        public bool IsVisible { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Export result;
    /// </summary>
    public class ExportResult;
    {
        public Guid ExportId { get; set; }
        public Guid ChartId { get; set; }
        public ExportFormat Format { get; set; }
        public byte[] Data { get; set; }
        public string FilePath { get; set; }
        public long SizeBytes { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan ExportTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Chart data validation result;
    /// </summary>
    public class ChartDataValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Chart render cache entry
    /// </summary>
    internal class ChartRenderCache;
    {
        public Guid ChartId { get; set; }
        public ChartResult Result { get; set; }
        public ChartOptions Options { get; set; }
        public DateTime CachedTime { get; set; }
    }

    /// <summary>
    /// Fixed size queue for real-time data;
    /// </summary>
    public class FixedSizeQueue<T> : Queue<T>
    {
        private readonly int _maxSize;

        public FixedSizeQueue(int maxSize)
        {
            _maxSize = maxSize;
        }

        public new void Enqueue(T item)
        {
            base.Enqueue(item);

            while (Count > _maxSize)
            {
                Dequeue();
            }
        }
    }

    /// <summary>
    /// Chart rendered event arguments;
    /// </summary>
    public class ChartRenderedEventArgs : EventArgs;
    {
        public Guid ChartId { get; set; }
        public ChartType ChartType { get; set; }
        public ChartResult Result { get; set; }
        public TimeSpan RenderTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Chart template created event arguments;
    /// </summary>
    public class ChartTemplateCreatedEventArgs : EventArgs;
    {
        public string TemplateName { get; set; }
        public ChartType ChartType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Chart theme applied event arguments;
    /// </summary>
    public class ChartThemeAppliedEventArgs : EventArgs;
    {
        public string ThemeName { get; set; }
        public ChartType ChartType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Chart builder specific exception;
    /// </summary>
    public class ChartBuilderException : Exception
    {
        public string ErrorCode { get; }

        public ChartBuilderException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public ChartBuilderException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Chart type not supported exception;
    /// </summary>
    public class ChartTypeNotSupportedException : Exception
    {
        public ChartTypeNotSupportedException(string message) : base(message) { }
    }

    /// <summary>
    /// Chart data exception;
    /// </summary>
    public class ChartDataException : Exception
    {
        public ChartDataException(string message) : base(message) { }
    }

    /// <summary>
    /// Export format not supported exception;
    /// </summary>
    public class ExportFormatNotSupportedException : Exception
    {
        public ExportFormatNotSupportedException(string message) : base(message) { }
    }

    /// <summary>
    /// Template not found exception;
    /// </summary>
    public class TemplateNotFoundException : Exception
    {
        public TemplateNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Chart error codes;
    /// </summary>
    public static class ChartErrorCodes;
    {
        public const string ChartCreationFailed = "CHART_001";
        public const string DashboardCreationFailed = "CHART_002";
        public const string RealTimeChartFailed = "CHART_003";
        public const string RealTimeUpdateFailed = "CHART_004";
        public const string ExportFailed = "CHART_005";
        public const string TemplateGenerationFailed = "CHART_006";
        public const string InteractiveChartFailed = "CHART_007";
        public const string InvalidChartData = "CHART_008";
        public const string ThemeNotFound = "CHART_009";
    }

    #endregion;
}
