// NEDA.UI/Views/TaskVisualizer/ProgressChart.xaml.cs;
using LiveCharts;
using LiveCharts.Wpf;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.Services.ProjectService;
using NEDA.UI.ViewModels.TaskVisualizer;
using Org.BouncyCastle.Asn1.Ocsp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NEDA.UI.Views.TaskVisualizer;
{
    public partial class ProgressChart : UserControl, INotifyPropertyChanged;
    {
        private readonly ILogger _logger;
        private readonly IProjectManager _projectManager;

        #region Dependency Properties;

        public static readonly DependencyProperty ChartTitleProperty =
            DependencyProperty.Register("ChartTitle", typeof(string), typeof(ProgressChart),
                new PropertyMetadata("Progress Overview", OnChartTitleChanged));

        public static readonly DependencyProperty ChartDataProperty =
            DependencyProperty.Register("ChartData", typeof(ObservableCollection<ChartDataPoint>),
                typeof(ProgressChart), new PropertyMetadata(null, OnChartDataChanged));

        public static readonly DependencyProperty SelectedTimeRangeProperty =
            DependencyProperty.Register("SelectedTimeRange", typeof(TimeRange),
                typeof(ProgressChart), new PropertyMetadata(TimeRange.Last7Days, OnTimeRangeChanged));

        public static readonly DependencyProperty ChartTypeProperty =
            DependencyProperty.Register("ChartType", typeof(ChartType),
                typeof(ProgressChart), new PropertyMetadata(ChartType.Bar, OnChartTypeChanged));

        #endregion;

        #region Properties;

        public string ChartTitle;
        {
            get => (string)GetValue(ChartTitleProperty);
            set => SetValue(ChartTitleProperty, value);
        }

        public ObservableCollection<ChartDataPoint> ChartData;
        {
            get => (ObservableCollection<ChartDataPoint>)GetValue(ChartDataProperty);
            set => SetValue(ChartDataProperty, value);
        }

        public TimeRange SelectedTimeRange;
        {
            get => (TimeRange)GetValue(SelectedTimeRangeProperty);
            set => SetValue(SelectedTimeRangeProperty, value);
        }

        public ChartType ChartType;
        {
            get => (ChartType)GetValue(ChartTypeProperty);
            set => SetValue(ChartTypeProperty, value);
        }

        // ViewModel properties for data binding;
        public SeriesCollection BarSeries { get; private set; }
        public SeriesCollection LineSeries { get; private set; }
        public SeriesCollection PieSeries { get; private set; }

        public ObservableCollection<string> XAxisLabels { get; private set; }
        public ObservableCollection<LegendItem> ChartLegendItems { get; private set; }

        public string XAxisTitle => "Tasks / Time";
        public string YAxisTitle => "Progress (%)";

        public bool IsBarChartVisible => ChartType == ChartType.Bar;
        public bool IsLineChartVisible => ChartType == ChartType.Line;
        public bool IsPieChartVisible => ChartType == ChartType.Pie;

        public int TotalTasks { get; private set; }
        public int CompletedTasks { get; private set; }
        public int InProgressTasks { get; private set; }
        public double OverallProgress { get; private set; }

        public string TimeRangeInfo { get; private set; }

        public bool HasData => ChartData?.Any() == true;

        public ObservableCollection<ChartTypeInfo> ChartTypes { get; }
        public ObservableCollection<TimeRange> TimeRanges { get; }

        #endregion;

        #region Constructor;

        public ProgressChart()
        {
            try
            {
                InitializeComponent();
                InitializeServices();
                InitializeChartData();
                InitializeCollections();

                Loaded += OnLoaded;
                Unloaded += OnUnloaded;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to initialize ProgressChart: {ex.Message}", ex);
                throw;
            }
        }

        public ProgressChart(ILogger logger, IProjectManager projectManager) : this()
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
        }

        private void InitializeServices()
        {
            // Try to resolve services if not provided via constructor;
            if (_logger == null)
            {
                _logger = ServiceLocator.GetService<ILogger>();
            }

            if (_projectManager == null)
            {
                _projectManager = ServiceLocator.GetService<IProjectManager>();
            }
        }

        private void InitializeCollections()
        {
            ChartTypes = new ObservableCollection<ChartTypeInfo>
            {
                new ChartTypeInfo { Type = ChartType.Bar, DisplayName = "Bar Chart", IconPath = "M3,18H21V16H3V18ZM3,13H21V11H3V13ZM3,6V8H21V6H3Z" },
                new ChartTypeInfo { Type = ChartType.Line, DisplayName = "Line Chart", IconPath = "M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5Z" },
                new ChartTypeInfo { Type = ChartType.Pie, DisplayName = "Pie Chart", IconPath = "M12,2A10,10 0 0,1 22,12A10,10 0 0,1 12,22A10,10 0 0,1 2,12A10,10 0 0,1 12,2M12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12A8,8 0 0,0 12,4Z" }
            };

            TimeRanges = new ObservableCollection<TimeRange>
            {
                TimeRange.Last24Hours,
                TimeRange.Last7Days,
                TimeRange.Last30Days,
                TimeRange.LastQuarter,
                TimeRange.LastYear,
                TimeRange.AllTime;
            };
        }

        #endregion;

        #region Event Handlers;

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ChartData == null || !ChartData.Any())
                {
                    LoadDefaultData();
                }

                RefreshChart();
                StartAutoRefresh();

                _logger?.LogInformation("ProgressChart loaded successfully");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error loading ProgressChart: {ex.Message}", ex);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopAutoRefresh();
        }

        private static void OnChartTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProgressChart chart)
            {
                chart.OnPropertyChanged(nameof(ChartTitle));
            }
        }

        private static void OnChartDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProgressChart chart)
            {
                chart.ProcessChartData();
                chart.RefreshChart();
            }
        }

        private static void OnTimeRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProgressChart chart && chart.IsLoaded)
            {
                chart.UpdateTimeRangeInfo();
                chart.RefreshChart();
            }
        }

        private static void OnChartTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ProgressChart chart)
            {
                chart.OnPropertyChanged(nameof(IsBarChartVisible));
                chart.OnPropertyChanged(nameof(IsLineChartVisible));
                chart.OnPropertyChanged(nameof(IsPieChartVisible));
            }
        }

        #endregion;

        #region Chart Methods;

        private void InitializeChartData()
        {
            BarSeries = new SeriesCollection();
            LineSeries = new SeriesCollection();
            PieSeries = new SeriesCollection();
            XAxisLabels = new ObservableCollection<string>();
            ChartLegendItems = new ObservableCollection<LegendItem>();

            ChartData = new ObservableCollection<ChartDataPoint>();
        }

        private void LoadDefaultData()
        {
            // Default sample data for demonstration;
            var defaultData = new List<ChartDataPoint>
            {
                new ChartDataPoint { Label = "Design", Value = 85, Category = "Development", Color = Colors.SteelBlue },
                new ChartDataPoint { Label = "Implementation", Value = 65, Category = "Development", Color = Colors.MediumSeaGreen },
                new ChartDataPoint { Label = "Testing", Value = 45, Category = "Quality", Color = Colors.Goldenrod },
                new ChartDataPoint { Label = "Deployment", Value = 25, Category = "Operations", Color = Colors.Tomato },
                new ChartDataPoint { Label = "Documentation", Value = 90, Category = "Support", Color = Colors.MediumPurple }
            };

            ChartData = new ObservableCollection<ChartDataPoint>(defaultData);
        }

        private void ProcessChartData()
        {
            if (ChartData == null || !ChartData.Any())
            {
                TotalTasks = 0;
                CompletedTasks = 0;
                InProgressTasks = 0;
                OverallProgress = 0;
                return;
            }

            TotalTasks = ChartData.Count;
            CompletedTasks = ChartData.Count(d => d.Value >= 100);
            InProgressTasks = ChartData.Count(d => d.Value > 0 && d.Value < 100);
            OverallProgress = ChartData.Average(d => d.Value);

            UpdateTimeRangeInfo();
            UpdateChartSeries();
            UpdateLegendItems();

            OnPropertyChanged(nameof(TotalTasks));
            OnPropertyChanged(nameof(CompletedTasks));
            OnPropertyChanged(nameof(InProgressTasks));
            OnPropertyChanged(nameof(OverallProgress));
            OnPropertyChanged(nameof(HasData));
        }

        private void UpdateChartSeries()
        {
            UpdateBarSeries();
            UpdateLineSeries();
            UpdatePieSeries();
        }

        private void UpdateBarSeries()
        {
            BarSeries.Clear();

            var columnSeries = new ColumnSeries;
            {
                Title = "Progress",
                Values = new ChartValues<double>(ChartData.Select(d => d.Value)),
                DataLabels = true,
                LabelPoint = point => $"{point.Y:F1}%",
                Fill = new SolidColorBrush(Colors.SteelBlue),
                Stroke = new SolidColorBrush(Colors.DarkSlateBlue),
                StrokeThickness = 1,
                MaxColumnWidth = 50;
            };

            BarSeries.Add(columnSeries);

            // Update X-axis labels;
            XAxisLabels.Clear();
            foreach (var data in ChartData)
            {
                XAxisLabels.Add(data.Label);
            }
        }

        private void UpdateLineSeries()
        {
            LineSeries.Clear();

            var lineSeries = new LineSeries;
            {
                Title = "Progress Trend",
                Values = new ChartValues<double>(ChartData.Select(d => d.Value)),
                PointGeometry = DefaultGeometries.Circle,
                PointGeometrySize = 10,
                PointForeground = Brushes.White,
                Stroke = new SolidColorBrush(Colors.MediumSeaGreen),
                StrokeThickness = 3,
                Fill = Brushes.Transparent;
            };

            LineSeries.Add(lineSeries);
        }

        private void UpdatePieSeries()
        {
            PieSeries.Clear();

            foreach (var data in ChartData)
            {
                var pieSeries = new PieSeries;
                {
                    Title = data.Label,
                    Values = new ChartValues<double> { data.Value },
                    DataLabels = true,
                    LabelPoint = point => $"{point.Y:F1}%",
                    Fill = new SolidColorBrush(data.Color),
                    Stroke = Brushes.White,
                    StrokeThickness = 2;
                };

                PieSeries.Add(pieSeries);
            }
        }

        private void UpdateLegendItems()
        {
            ChartLegendItems.Clear();

            foreach (var data in ChartData)
            {
                ChartLegendItems.Add(new LegendItem;
                {
                    Label = data.Label,
                    Value = data.Value,
                    Color = new SolidColorBrush(data.Color),
                    Category = data.Category;
                });
            }
        }

        private void UpdateTimeRangeInfo()
        {
            TimeRangeInfo = SelectedTimeRange switch;
            {
                TimeRange.Last24Hours => "Last 24 Hours",
                TimeRange.Last7Days => "Last 7 Days",
                TimeRange.Last30Days => "Last 30 Days",
                TimeRange.LastQuarter => "Last Quarter",
                TimeRange.LastYear => "Last Year",
                TimeRange.AllTime => "All Time",
                _ => "Custom Range"
            };

            OnPropertyChanged(nameof(TimeRangeInfo));
        }

        public void RefreshChart()
        {
            if (!CheckAccess())
            {
                Dispatcher.Invoke(RefreshChart);
                return;
            }

            try
            {
                ProcessChartData();

                OnPropertyChanged(nameof(BarSeries));
                OnPropertyChanged(nameof(LineSeries));
                OnPropertyChanged(nameof(PieSeries));
                OnPropertyChanged(nameof(XAxisLabels));
                OnPropertyChanged(nameof(ChartLegendItems));

                _logger?.LogDebug("ProgressChart refreshed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error refreshing chart: {ex.Message}", ex);
            }
        }

        public void ClearChart()
        {
            ChartData?.Clear();
            BarSeries?.Clear();
            LineSeries?.Clear();
            PieSeries?.Clear();
            XAxisLabels?.Clear();
            ChartLegendItems?.Clear();

            RefreshChart();
        }

        #endregion;

        #region Auto Refresh;

        private System.Timers.Timer _refreshTimer;

        private void StartAutoRefresh()
        {
            _refreshTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds);
            _refreshTimer.Elapsed += (s, e) => Dispatcher.Invoke(RefreshChart);
            _refreshTimer.Start();
        }

        private void StopAutoRefresh()
        {
            _refreshTimer?.Stop();
            _refreshTimer?.Dispose();
            _refreshTimer = null;
        }

        #endregion;

        #region INotifyPropertyChanged;

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion;
    }

    #region Supporting Classes;

    public class ChartDataPoint;
    {
        public string Label { get; set; }
        public double Value { get; set; }
        public string Category { get; set; }
        public Color Color { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    public class LegendItem;
    {
        public string Label { get; set; }
        public double Value { get; set; }
        public Brush Color { get; set; }
        public string Category { get; set; }
    }

    public class ChartTypeInfo;
    {
        public ChartType Type { get; set; }
        public string DisplayName { get; set; }
        public string IconPath { get; set; }
    }

    public enum ChartType;
    {
        Bar,
        Line,
        Pie;
    }

    public enum TimeRange;
    {
        Last24Hours,
        Last7Days,
        Last30Days,
        LastQuarter,
        LastYear,
        AllTime;
    }

    #endregion;
}
