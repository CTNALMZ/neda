using NEDA.Core.Engine;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.VisualInterface.MainDashboard.Contracts;
using NEDA.Monitoring.HealthChecks;
using NEDA.Monitoring.PerformanceCounters;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NEDA.Interface.VisualInterface.MainDashboard;
{
    /// <summary>
    /// StatusDisplay.xaml için etkileşim mantığı;
    /// </summary>
    public partial class StatusDisplay : INotifyPropertyChanged;
    {
        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly IHealthChecker _healthChecker;
        private readonly StatusDisplayConfiguration _configuration;

        private CancellationTokenSource _refreshCts;
        private System.Timers.Timer _autoRefreshTimer;
        private bool _isInitialized;
        private DateTime _lastUpdateTime;

        public StatusDisplay(
            ILogger logger,
            IErrorHandler errorHandler,
            IPerformanceMonitor performanceMonitor,
            IHealthChecker healthChecker,
            StatusDisplayConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
            _configuration = configuration ?? StatusDisplayConfiguration.Default;

            InitializeComponent();
            DataContext = this;

            // Komutları başlat;
            InitializeCommands();

            // Koleksiyonları başlat;
            StatusItems = new ObservableCollection<StatusItemViewModel>();

            // Varsayılan değerleri ayarla;
            SetDefaultValues();

            // Auto-refresh timer'ını başlat;
            InitializeAutoRefresh();
        }

        #region Properties;

        public ObservableCollection<StatusItemViewModel> StatusItems { get; private set; }

        private DateTime _lastUpdateTime;
        public DateTime LastUpdateTime;
        {
            get => _lastUpdateTime;
            set => SetField(ref _lastUpdateTime, value);
        }

        private double _cpuUsage;
        public double CpuUsage;
        {
            get => _cpuUsage;
            set => SetField(ref _cpuUsage, value);
        }

        private double _memoryUsage;
        public double MemoryUsage;
        {
            get => _memoryUsage;
            set => SetField(ref _memoryUsage, value);
        }

        private double _diskUsage;
        public double DiskUsage;
        {
            get => _diskUsage;
            set => SetField(ref _diskUsage, value);
        }

        private double _networkSpeed;
        public double NetworkSpeed;
        {
            get => _networkSpeed;
            set => SetField(ref _networkSpeed, value);
        }

        private string _networkStatus;
        public string NetworkStatus;
        {
            get => _networkStatus;
            set => SetField(ref _networkStatus, value);
        }

        private SystemHealthViewModel _systemHealth;
        public SystemHealthViewModel SystemHealth;
        {
            get => _systemHealth;
            set => SetField(ref _systemHealth, value);
        }

        private ConnectionStatusViewModel _connectionStatus;
        public ConnectionStatusViewModel ConnectionStatus;
        {
            get => _connectionStatus;
            set => SetField(ref _connectionStatus, value);
        }

        private int _unreadNotificationCount;
        public int UnreadNotificationCount;
        {
            get => _unreadNotificationCount;
            set => SetField(ref _unreadNotificationCount, value);
        }

        private int _totalNotificationCount;
        public int TotalNotificationCount;
        {
            get => _totalNotificationCount;
            set => SetField(ref _totalNotificationCount, value);
        }

        private bool _hasCriticalNotifications;
        public bool HasCriticalNotifications;
        {
            get => _hasCriticalNotifications;
            set => SetField(ref _hasCriticalNotifications, value);
        }

        private bool _isLoading;
        public bool IsLoading;
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        private bool _isRefreshing;
        public bool IsRefreshing;
        {
            get => _isRefreshing;
            set => SetField(ref _isRefreshing, value);
        }

        private bool _hasError;
        public bool HasError;
        {
            get => _hasError;
            set => SetField(ref _hasError, value);
        }

        private string _errorMessage;
        public string ErrorMessage;
        {
            get => _errorMessage;
            set => SetField(ref _errorMessage, value);
        }

        #endregion;

        #region Commands;

        public ICommand RefreshCommand { get; private set; }
        public ICommand ViewNotificationsCommand { get; private set; }
        public ICommand SettingsCommand { get; private set; }
        public ICommand RetryCommand { get; private set; }

        private void InitializeCommands()
        {
            RefreshCommand = new RelayCommand(async () => await RefreshAsync());
            ViewNotificationsCommand = new RelayCommand(() => ViewNotifications());
            SettingsCommand = new RelayCommand(() => OpenSettings());
            RetryCommand = new RelayCommand(async () => await RefreshAsync());
        }

        #endregion;

        #region Public Methods;

        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                IsLoading = true;
                _logger.LogInformation("Initializing StatusDisplay...");

                // İlk verileri yükle;
                await LoadInitialDataAsync();

                // Auto-refresh timer'ını başlat;
                _autoRefreshTimer.Start();

                _isInitialized = true;
                IsLoading = false;

                _logger.LogInformation("StatusDisplay initialized successfully");
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = $"Başlatma hatası: {ex.Message}";
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "StatusDisplay",
                    Operation = "Initialize"
                });
            }
        }

        public async Task RefreshAsync()
        {
            if (IsRefreshing)
                return;

            try
            {
                IsRefreshing = true;
                HasError = false;

                // Mevcut işlemleri iptal et;
                _refreshCts?.Cancel();
                _refreshCts = new CancellationTokenSource();

                _logger.LogDebug("Refreshing StatusDisplay...");

                // Performans metriklerini güncelle;
                await UpdatePerformanceMetricsAsync(_refreshCts.Token);

                // Sistem sağlığını güncelle;
                await UpdateSystemHealthAsync(_refreshCts.Token);

                // Bağlantı durumunu güncelle;
                await UpdateConnectionStatusAsync(_refreshCts.Token);

                // Bildirim istatistiklerini güncelle;
                await UpdateNotificationStatsAsync(_refreshCts.Token);

                // Durum öğelerini güncelle;
                await UpdateStatusItemsAsync(_refreshCts.Token);

                LastUpdateTime = DateTime.Now;
                IsRefreshing = false;

                _logger.LogDebug("StatusDisplay refresh completed");
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("StatusDisplay refresh cancelled");
                IsRefreshing = false;
            }
            catch (Exception ex)
            {
                HasError = true;
                ErrorMessage = $"Yenileme hatası: {ex.Message}";
                IsRefreshing = false;

                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "StatusDisplay",
                    Operation = "Refresh"
                });
            }
        }

        public void Shutdown()
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer?.Dispose();

            _refreshCts?.Cancel();
            _refreshCts?.Dispose();

            _logger.LogInformation("StatusDisplay shutdown");
        }

        #endregion;

        #region Private Methods;

        private void SetDefaultValues()
        {
            SystemHealth = new SystemHealthViewModel;
            {
                Status = "Bilinmiyor",
                Message = "Yükleniyor...",
                Icon = "HelpCircle",
                Color = Brushes.Gray;
            };

            ConnectionStatus = new ConnectionStatusViewModel;
            {
                Message = "Bağlanıyor...",
                Details = "Bağlantı durumu kontrol ediliyor",
                Icon = "CloudSync",
                Color = Brushes.Gray;
            };

            // Varsayılan durum öğeleri;
            StatusItems.Add(new StatusItemViewModel;
            {
                Title = "CPU Kullanımı",
                Value = "0.0",
                Unit = "%",
                Percentage = 0,
                Icon = "Cpu",
                StatusColor = Brushes.Blue;
            });

            StatusItems.Add(new StatusItemViewModel;
            {
                Title = "Bellek",
                Value = "0.0",
                Unit = "GB",
                Percentage = 0,
                Icon = "Memory",
                StatusColor = Brushes.Green;
            });

            StatusItems.Add(new StatusItemViewModel;
            {
                Title = "Disk",
                Value = "0.0",
                Unit = "GB",
                Percentage = 0,
                Icon = "Harddisk",
                StatusColor = Brushes.Orange;
            });

            StatusItems.Add(new StatusItemViewModel;
            {
                Title = "Ağ",
                Value = "0.0",
                Unit = "Mbps",
                Percentage = 0,
                Icon = "Network",
                StatusColor = Brushes.Purple;
            });

            StatusItems.Add(new StatusItemViewModel;
            {
                Title = "Süreçler",
                Value = "0",
                Unit = "Aktif",
                Percentage = 0,
                Icon = "Application",
                StatusColor = Brushes.Teal;
            });

            StatusItems.Add(new StatusItemViewModel;
            {
                Title = "Hata Oranı",
                Value = "0.0",
                Unit = "%",
                Percentage = 0,
                Icon = "Alert",
                StatusColor = Brushes.Red;
            });
        }

        private void InitializeAutoRefresh()
        {
            _autoRefreshTimer = new System.Timers.Timer(_configuration.AutoRefreshInterval.TotalMilliseconds);
            _autoRefreshTimer.Elapsed += async (s, e) => await AutoRefreshAsync();
            _autoRefreshTimer.AutoReset = true;
        }

        private async Task AutoRefreshAsync()
        {
            if (IsRefreshing || !_isInitialized)
                return;

            await Application.Current.Dispatcher.InvokeAsync(async () =>
            {
                await RefreshAsync();
            });
        }

        private async Task LoadInitialDataAsync()
        {
            await RefreshAsync();
        }

        private async Task UpdatePerformanceMetricsAsync(CancellationToken cancellationToken)
        {
            try
            {
                var metrics = await _performanceMonitor.GetSystemMetricsAsync(cancellationToken);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    CpuUsage = metrics.CpuUsage;
                    MemoryUsage = metrics.MemoryUsage;
                    DiskUsage = metrics.DiskUsage;
                    NetworkSpeed = metrics.NetworkIn + metrics.NetworkOut;
                    NetworkStatus = GetNetworkStatus(metrics.NetworkIn, metrics.NetworkOut);

                    // StatusItems güncelleme;
                    UpdateStatusItem(0, CpuUsage, CpuUsage.ToString("F1"));
                    UpdateStatusItem(1, MemoryUsage, (metrics.MemoryUsage * 16).ToString("F1")); // Örnek: 16GB toplam bellek;
                    UpdateStatusItem(2, DiskUsage, (metrics.DiskUsage * 500).ToString("F1")); // Örnek: 500GB toplam disk;
                    UpdateStatusItem(3, Math.Min(100, NetworkSpeed / 10), NetworkSpeed.ToString("F1")); // 10Mbps = %100;
                    UpdateStatusItem(4, metrics.ActiveProcesses / 100.0, metrics.ActiveProcesses.ToString()); // 100 işlem = %100;
                    UpdateStatusItem(5, metrics.ErrorRate * 100, metrics.ErrorRate.ToString("P1"));
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to update performance metrics: {ex.Message}");
                throw;
            }
        }

        private async Task UpdateSystemHealthAsync(CancellationToken cancellationToken)
        {
            try
            {
                var health = await _healthChecker.CheckSystemHealthAsync(cancellationToken);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    SystemHealth = new SystemHealthViewModel;
                    {
                        Status = health.OverallStatus.ToString(),
                        Message = health.Message ?? "Sistem sağlıklı çalışıyor",
                        Icon = GetHealthIcon(health.OverallStatus),
                        Color = GetHealthColor(health.OverallStatus)
                    };
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to update system health: {ex.Message}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    SystemHealth = new SystemHealthViewModel;
                    {
                        Status = "Hata",
                        Message = $"Sağlık kontrolü başarısız: {ex.Message}",
                        Icon = "AlertCircle",
                        Color = Brushes.Red;
                    };
                });
            }
        }

        private async Task UpdateConnectionStatusAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Bağlantı testi yap;
                bool isConnected = await TestConnectionAsync(cancellationToken);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (isConnected)
                    {
                        ConnectionStatus = new ConnectionStatusViewModel;
                        {
                            Message = "Bağlı",
                            Details = "Tüm servislere bağlantı mevcut",
                            Icon = "CloudCheck",
                            Color = Brushes.Green;
                        };
                    }
                    else;
                    {
                        ConnectionStatus = new ConnectionStatusViewModel;
                        {
                            Message = "Kısıtlı Bağlantı",
                            Details = "Bazı servislere bağlanılamıyor",
                            Icon = "CloudOff",
                            Color = Brushes.Orange;
                        };
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to update connection status: {ex.Message}");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ConnectionStatus = new ConnectionStatusViewModel;
                    {
                        Message = "Bağlantı Hatası",
                        Details = $"Bağlantı testi başarısız: {ex.Message}",
                        Icon = "CloudAlert",
                        Color = Brushes.Red;
                    };
                });
            }
        }

        private async Task UpdateNotificationStatsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Bildirim istatistiklerini al;
                // Bu örnekte sabit değerler kullanılıyor, gerçek implementasyonda notifier servisi kullanılır;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    UnreadNotificationCount = 5; // Örnek değer;
                    TotalNotificationCount = 23; // Örnek değer;
                    HasCriticalNotifications = UnreadNotificationCount > 0;
                });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to update notification stats: {ex.Message}");
            }
        }

        private async Task UpdateStatusItemsAsync(CancellationToken cancellationToken)
        {
            // Trend bilgilerini güncelle;
            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var item in StatusItems)
                {
                    // Trend simülasyonu;
                    if (item.Percentage > 80)
                    {
                        item.TrendIcon = "ArrowUp";
                        item.TrendColor = Brushes.Red;
                        item.ShowTrend = true;
                    }
                    else if (item.Percentage > 50)
                    {
                        item.TrendIcon = "ArrowUp";
                        item.TrendColor = Brushes.Orange;
                        item.ShowTrend = true;
                    }
                    else;
                    {
                        item.TrendIcon = "ArrowDown";
                        item.TrendColor = Brushes.Green;
                        item.ShowTrend = true;
                    }
                }
            });

            await Task.CompletedTask;
        }

        private void UpdateStatusItem(int index, double percentage, string value)
        {
            if (index >= 0 && index < StatusItems.Count)
            {
                var item = StatusItems[index];
                item.Percentage = percentage;
                item.Value = value;

                // Renk güncellemesi;
                item.StatusColor = GetPercentageColor(percentage);
            }
        }

        private async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
        {
            // Basit bağlantı testi;
            try
            {
                // Örnek: Google DNS'e ping at;
                using (var client = new System.Net.NetworkInformation.Ping())
                {
                    var reply = await client.SendPingAsync("8.8.8.8", 2000);
                    return reply.Status == System.Net.NetworkInformation.IPStatus.Success;
                }
            }
            catch
            {
                return false;
            }
        }

        private string GetNetworkStatus(double inSpeed, double outSpeed)
        {
            var totalSpeed = inSpeed + outSpeed;

            if (totalSpeed > 50)
                return "Yüksek";
            else if (totalSpeed > 10)
                return "Normal";
            else if (totalSpeed > 1)
                return "Düşük";
            else;
                return "Bağlı Değil";
        }

        private string GetHealthIcon(string healthStatus)
        {
            return healthStatus.ToLower() switch;
            {
                "healthy" => "CheckCircle",
                "warning" => "AlertCircle",
                "error" => "CloseCircle",
                _ => "HelpCircle"
            };
        }

        private Brush GetHealthColor(string healthStatus)
        {
            return healthStatus.ToLower() switch;
            {
                "healthy" => Brushes.Green,
                "warning" => Brushes.Orange,
                "error" => Brushes.Red,
                _ => Brushes.Gray;
            };
        }

        private Brush GetPercentageColor(double percentage)
        {
            if (percentage >= 90)
                return Brushes.Red;
            else if (percentage >= 70)
                return Brushes.Orange;
            else if (percentage >= 50)
                return Brushes.Yellow;
            else;
                return Brushes.Green;
        }

        private void ViewNotifications()
        {
            _logger.LogInformation("View notifications requested");
            // Bildirim penceresini aç;
            // MessageBox.Show("Bildirimler görüntüleniyor...");
        }

        private void OpenSettings()
        {
            _logger.LogInformation("Open settings requested");
            // Ayarlar penceresini aç;
            // MessageBox.Show("Ayarlar açılıyor...");
        }

        #endregion;

        #region Event Handlers;

        private void StatusDisplay_Loaded(object sender, RoutedEventArgs e)
        {
            _ = InitializeAsync();
        }

        private void StatusDisplay_Unloaded(object sender, RoutedEventArgs e)
        {
            Shutdown();
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
    }

    #region ViewModel Classes;

    public class StatusItemViewModel : INotifyPropertyChanged;
    {
        private string _title;
        public string Title;
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        private string _value;
        public string Value;
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        private string _unit;
        public string Unit;
        {
            get => _unit;
            set => SetField(ref _unit, value);
        }

        private double _percentage;
        public double Percentage;
        {
            get => _percentage;
            set => SetField(ref _percentage, value);
        }

        private string _icon;
        public string Icon;
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        private Brush _statusColor;
        public Brush StatusColor;
        {
            get => _statusColor;
            set => SetField(ref _statusColor, value);
        }

        private string _trendIcon;
        public string TrendIcon;
        {
            get => _trendIcon;
            set => SetField(ref _trendIcon, value);
        }

        private Brush _trendColor;
        public Brush TrendColor;
        {
            get => _trendColor;
            set => SetField(ref _trendColor, value);
        }

        private bool _showTrend;
        public bool ShowTrend;
        {
            get => _showTrend;
            set => SetField(ref _showTrend, value);
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

    public class SystemHealthViewModel : INotifyPropertyChanged;
    {
        private string _status;
        public string Status;
        {
            get => _status;
            set => SetField(ref _status, value);
        }

        private string _message;
        public string Message;
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        private string _icon;
        public string Icon;
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        private Brush _color;
        public Brush Color;
        {
            get => _color;
            set => SetField(ref _color, value);
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

    public class ConnectionStatusViewModel : INotifyPropertyChanged;
    {
        private string _message;
        public string Message;
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        private string _details;
        public string Details;
        {
            get => _details;
            set => SetField(ref _details, value);
        }

        private string _icon;
        public string Icon;
        {
            get => _icon;
            set => SetField(ref _icon, value);
        }

        private Brush _color;
        public Brush Color;
        {
            get => _color;
            set => SetField(ref _color, value);
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

    public class StatusDisplayConfiguration;
    {
        public static StatusDisplayConfiguration Default => new StatusDisplayConfiguration();

        public TimeSpan AutoRefreshInterval { get; set; } = TimeSpan.FromSeconds(10);
        public int MaxStatusItems { get; set; } = 6;
        public bool EnableAnimations { get; set; } = true;
        public bool ShowTrendIndicators { get; set; } = true;
        public double WarningThreshold { get; set; } = 70.0;
        public double CriticalThreshold { get; set; } = 90.0;
    }

    #endregion;
}

namespace NEDA.Interface.VisualInterface.MainDashboard.Contracts;
{
    /// <summary>
    /// StatusDisplay servisi interface'i;
    /// </summary>
    public interface IStatusDisplay;
    {
        Task InitializeAsync();
        Task RefreshAsync();
        void Shutdown();
    }
}

namespace NEDA.UI.Converters;
{
    public class BooleanToVisibilityConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is Visibility v && v == Visibility.Visible;
        }
    }

    public class PercentageToColorConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double percentage)
            {
                if (percentage >= 90) return Brushes.Red;
                if (percentage >= 70) return Brushes.Orange;
                if (percentage >= 50) return Brushes.Yellow;
                return Brushes.Green;
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StatusToIconConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString()?.ToLower() switch;
            {
                "healthy" => "CheckCircle",
                "warning" => "AlertCircle",
                "error" => "CloseCircle",
                _ => "HelpCircle"
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class DateTimeToRelativeTimeConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DateTime dateTime)
            {
                var span = DateTime.Now - dateTime;

                if (span.TotalSeconds < 60)
                    return "Az önce";
                if (span.TotalMinutes < 60)
                    return $"{(int)span.TotalMinutes} dakika önce";
                if (span.TotalHours < 24)
                    return $"{(int)span.TotalHours} saat önce";
                if (span.TotalDays < 30)
                    return $"{(int)span.TotalDays} gün önce";

                return dateTime.ToString("dd.MM.yyyy HH:mm");
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BytesToHumanReadableConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is double bytes)
            {
                string[] sizes = { "B", "KB", "MB", "GB", "TB" };
                int order = 0;
                while (bytes >= 1024 && order < sizes.Length - 1)
                {
                    order++;
                    bytes = bytes / 1024;
                }
                return $"{bytes:0.##} {sizes[order]}";
            }
            return "0 B";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class NotificationCountToBadgeConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count && count > 0)
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class HealthStatusToBrushConverter : IValueConverter;
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.ToString()?.ToLower() switch;
            {
                "high" or "yüksek" => Brushes.Green,
                "normal" => Brushes.Blue,
                "low" or "düşük" => Brushes.Orange,
                "bağlı değil" => Brushes.Red,
                _ => Brushes.Gray;
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

// RelayCommand Implementation;
public class RelayCommand : ICommand;
{
    private readonly Action _execute;
    private readonly Func<bool> _canExecute;

    public RelayCommand(Action execute, Func<bool> canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler CanExecuteChanged;
    {
        add { CommandManager.RequerySuggested += value; }
        remove { CommandManager.RequerySuggested -= value; }
    }

    public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object parameter) => _execute();
}
