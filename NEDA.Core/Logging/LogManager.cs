// NEDA.Core/Logging/LogManager.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;

namespace NEDA.Core.Logging
{
    /// <summary>
    /// Merkezi log yönetim sistemi - Singleton tasarım deseni ile implemente edilmiştir;
    /// </summary>
    public sealed class LogManager : ILogManager, IDisposable
    {
        #region Singleton Implementation

        private static readonly Lazy<LogManager> _instance =
            new Lazy<LogManager>(() => new LogManager());

        public static LogManager Instance => _instance.Value;

        private LogManager()
        {
            Initialize();
        }

        #endregion

        #region Fields and Properties

        private readonly ConcurrentDictionary<string, ILogger> _loggers =
            new ConcurrentDictionary<string, ILogger>();

        private readonly ConcurrentQueue<LogEntry> _logQueue =
            new ConcurrentQueue<LogEntry>();

        private readonly List<ILogSink> _logSinks = new List<ILogSink>();
        private readonly SemaphoreSlim _processingLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancellationTokenSource =
            new CancellationTokenSource();

        private Task _backgroundProcessingTask;
        private bool _isInitialized = false;
        private bool _isDisposed = false;

        private AppConfig _appConfig;
        private IRecoveryEngine _recoveryEngine;

        public LogLevel MinimumLogLevel { get; private set; } = LogLevel.Information;
        public bool IsProcessingEnabled { get; private set; } = true;
        public event EventHandler<LogEventArgs> LogWritten;

        #endregion

        #region Public Methods

        /// <summary>
        /// LogManager'ı başlatır ve yapılandırır;
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            try
            {
                _processingLock.Wait();

                // Yapılandırma yükleniyor;
                LoadConfiguration();

                // Log sink'lerini başlat;
                InitializeLogSinks();

                // Arka plan işleme görevini başlat;
                StartBackgroundProcessing();

                // Recovery engine başlat;
                InitializeRecoveryEngine();

                _isInitialized = true;

                LogInternal(LogLevel.Information, "LogManager başarıyla başlatıldı",
                    "LogManager", null, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                HandleInitializationError(ex);
                throw new LogManagerInitializationException(
                    "LogManager başlatma sırasında hata oluştu", ex);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Belirtilen kategori için logger alır veya oluşturur;
        /// </summary>
        public ILogger GetLogger(string category)
        {
            ValidateDisposed();
            ValidateCategory(category);

            return _loggers.GetOrAdd(category, cat =>
            {
                // Bu Logger tipi senin projendeki gerçek implementasyonla eşleşmeli
                var logger = Logger.CreateLogger(cat);
                return logger;
            });
        }

        /// <summary>
        /// T tipine özgü logger alır;
        /// </summary>
        public ILogger<T> GetLogger<T>() where T : class
        {
            ValidateDisposed();

            var category = typeof(T).FullName;
            return (ILogger<T>)_loggers.GetOrAdd(category, cat =>
            {
                var logger = Logger.CreateLogger<T>();
                return logger;
            });
        }

        /// <summary>
        /// Manuel olarak log yazar;
        /// </summary>
        public void WriteLog(LogLevel level, string message, string category,
            Exception exception = null, Dictionary<string, object> properties = null)
        {
            ValidateDisposed();

            if (!ShouldLog(level)) return;

            var logEntry = new LogEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = DateTime.UtcNow,
                Level = level,
                Category = category ?? "System",
                Message = message ?? string.Empty,
                Exception = exception,
                Properties = properties ?? new Dictionary<string, object>(),
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                ProcessId = Environment.ProcessId
            };

            EnqueueLogEntry(logEntry);
        }

        /// <summary>
        /// Log seviyesini dinamik olarak değiştirir;
        /// </summary>
        public void SetMinimumLogLevel(LogLevel level)
        {
            ValidateDisposed();

            MinimumLogLevel = level;

            LogInternal(LogLevel.Information,
                $"Minimum log seviyesi {level} olarak değiştirildi",
                "LogManager", null, DateTime.UtcNow);
        }

        /// <summary>
        /// Tüm logları temizler;
        /// </summary>
        public async Task ClearLogsAsync()
        {
            ValidateDisposed();

            await _processingLock.WaitAsync();
            try
            {
                while (_logQueue.TryDequeue(out _)) { }

                foreach (var sink in _logSinks)
                {
                    await sink.ClearAsync();
                }

                LogInternal(LogLevel.Information, "Tüm loglar temizlendi",
                    "LogManager", null, DateTime.UtcNow);
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Sistem durumunu alır;
        /// </summary>
        public LogManagerStatus GetStatus()
        {
            ValidateDisposed();

            return new LogManagerStatus
            {
                IsInitialized = _isInitialized,
                IsProcessingEnabled = IsProcessingEnabled,
                ActiveLoggersCount = _loggers.Count,
                QueuedLogsCount = _logQueue.Count,
                ActiveSinksCount = _logSinks.Count(s => s.IsEnabled),
                MinimumLogLevel = MinimumLogLevel,
                BackgroundTaskStatus = _backgroundProcessingTask?.Status ?? TaskStatus.Created
            };
        }

        /// <summary>
        /// Logları filtreleyerek alır;
        /// </summary>
        public async Task<IEnumerable<LogEntry>> GetLogsAsync(LogFilter filter)
        {
            ValidateDisposed();

            var results = new List<LogEntry>();

            foreach (var sink in _logSinks.Where(s => s.CanQuery))
            {
                var logs = await sink.QueryLogsAsync(filter);
                results.AddRange(logs);
            }

            return results.OrderByDescending(l => l.Timestamp);
        }

        /// <summary>
        /// Log işlemlerini duraklatır;
        /// </summary>
        public void PauseProcessing()
        {
            ValidateDisposed();

            IsProcessingEnabled = false;
            LogInternal(LogLevel.Warning, "Log işleme duraklatıldı",
                "LogManager", null, DateTime.UtcNow);
        }

        /// <summary>
        /// Log işlemlerini devam ettirir;
        /// </summary>
        public void ResumeProcessing()
        {
            ValidateDisposed();

            IsProcessingEnabled = true;
            LogInternal(LogLevel.Information, "Log işleme devam ettirildi",
                "LogManager", null, DateTime.UtcNow);
        }

        #endregion

        #region Internal Methods (Logger'lar tarafından kullanılır)

        internal void EnqueueLog(LogEntry logEntry)
        {
            if (!ShouldLog(logEntry.Level)) return;

            EnqueueLogEntry(logEntry);
        }

        #endregion

        #region Private Methods

        private void LoadConfiguration()
        {
            try
            {
                var serviceProvider = ServiceProviderFactory.GetServiceProvider();
                _appConfig = serviceProvider.GetRequiredService<AppConfig>();

                MinimumLogLevel = _appConfig.Logging.MinimumLogLevel;
            }
            catch
            {
                // Varsayılan değerler;
                MinimumLogLevel = LogLevel.Information;
            }
        }

        private void InitializeLogSinks()
        {
            // File Log Sink;
            var fileSink = new FileLogSink(_appConfig.Logging.FileLogPath);
            _logSinks.Add(fileSink);

            // Database Log Sink;
            if (_appConfig.Logging.EnableDatabaseLogging)
            {
                var dbSink = new DatabaseLogSink(_appConfig.ConnectionStrings.Default);
                _logSinks.Add(dbSink);
            }

            // Event Log Sink;
            if (_appConfig.Logging.EnableEventLog)
            {
                var eventLogSink = new EventLogSink(_appConfig.Application.Name);
                _logSinks.Add(eventLogSink);
            }

            // Console Log Sink (development ortamında)
            if (_appConfig.Environment.IsDevelopment)
            {
                var consoleSink = new ConsoleLogSink();
                _logSinks.Add(consoleSink);
            }
        }

        private void InitializeRecoveryEngine()
        {
            try
            {
                var serviceProvider = ServiceProviderFactory.GetServiceProvider();
                _recoveryEngine = serviceProvider.GetService<IRecoveryEngine>();
            }
            catch
            {
                // Recovery engine yoksa varsayılan bir tane oluştur;
                _recoveryEngine = new DefaultRecoveryEngine();
            }
        }

        private void StartBackgroundProcessing()
        {
            _backgroundProcessingTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        if (IsProcessingEnabled && _logQueue.TryDequeue(out var logEntry))
                        {
                            await ProcessLogEntryAsync(logEntry);
                        }
                        else
                        {
                            await Task.Delay(100, _cancellationTokenSource.Token);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // İptal edildi, normal çıkış;
                        break;
                    }
                    catch (Exception ex)
                    {
                        await HandleProcessingErrorAsync(ex);
                        await Task.Delay(1000, _cancellationTokenSource.Token);
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private async Task ProcessLogEntryAsync(LogEntry logEntry)
        {
            var successfulSinks = 0;
            var failedSinks = new List<string>();

            foreach (var sink in _logSinks.Where(s => s.IsEnabled))
            {
                try
                {
                    await sink.WriteAsync(logEntry);
                    successfulSinks++;
                }
                catch (Exception ex)
                {
                    failedSinks.Add($"{sink.GetType().Name}: {ex.Message}");

                    // Sink'i geçici olarak devre dışı bırak;
                    if (sink is IRecoverableSink recoverableSink)
                    {
                        await recoverableSink.DisableTemporarilyAsync(TimeSpan.FromMinutes(5));
                    }
                }
            }

            // Log event'ini tetikle;
            OnLogWritten(logEntry, successfulSinks, failedSinks);

            // Başarısız sink'ler varsa recovery mekanizmasını tetikle;
            if (failedSinks.Any() && _recoveryEngine != null)
            {
                await _recoveryEngine.AttemptRecoveryAsync(
                    new LogSinkFailureContext(failedSinks, logEntry));
            }
        }

        private void EnqueueLogEntry(LogEntry logEntry)
        {
            // Kuyruk boyutunu kontrol et;
            if (_logQueue.Count >= _appConfig.Logging.MaxQueueSize)
            {
                HandleQueueOverflow();
                return;
            }

            _logQueue.Enqueue(logEntry);

            // Kritik loglar için hemen işle;
            if (logEntry.Level >= LogLevel.Error)
            {
                Task.Run(() => ProcessCriticalLogImmediately(logEntry));
            }
        }

        private async void ProcessCriticalLogImmediately(LogEntry logEntry)
        {
            try
            {
                // Kritik logları hemen yaz;
                var criticalSinks = _logSinks
                    .Where(s => s.IsEnabled && s.SupportsImmediateWrite)
                    .ToList();

                foreach (var sink in criticalSinks)
                {
                    await sink.WriteImmediateAsync(logEntry);
                }
            }
            catch (Exception ex)
            {
                // Kritik log hatası - doğrudan konsola yaz;
                Console.WriteLine($"[CRITICAL LOG FAILURE] {DateTime.UtcNow:O}: {ex.Message}");
            }
        }

        private bool ShouldLog(LogLevel level)
        {
            return level >= MinimumLogLevel;
        }

        private void LogInternal(LogLevel level, string message,
            string category, Exception exception, DateTime timestamp)
        {
            var logEntry = new LogEntry
            {
                Id = Guid.NewGuid(),
                Timestamp = timestamp,
                Level = level,
                Category = category,
                Message = message,
                Exception = exception,
                ThreadId = Thread.CurrentThread.ManagedThreadId,
                ProcessId = Environment.ProcessId
            };

            EnqueueLogEntry(logEntry);
        }

        private void OnLogWritten(LogEntry logEntry, int successfulSinks,
            List<string> failedSinks)
        {
            LogWritten?.Invoke(this, new LogEventArgs
            {
                LogEntry = logEntry,
                SuccessfulSinks = successfulSinks,
                FailedSinks = failedSinks,
                Timestamp = DateTime.UtcNow
            });
        }

        private void HandleQueueOverflow()
        {
            // Eski logları temizle (FIFO)
            for (int i = 0; i < _appConfig.Logging.OverflowCleanupCount; i++)
            {
                _logQueue.TryDequeue(out _);
            }

            LogInternal(LogLevel.Warning,
                $"Log kuyruğu taştı, {_appConfig.Logging.OverflowCleanupCount} eski log temizlendi",
                "LogManager", null, DateTime.UtcNow);
        }

        private async Task HandleProcessingErrorAsync(Exception exception)
        {
            // Hata logla;
            LogInternal(LogLevel.Error,
                $"Log işleme hatası: {exception.Message}",
                "LogManager", exception, DateTime.UtcNow);

            // Recovery mekanizmasını tetikle;
            if (_recoveryEngine != null)
            {
                await _recoveryEngine.AttemptRecoveryAsync(
                    new LogProcessingFailureContext(exception));
            }
        }

        private void HandleInitializationError(Exception exception)
        {
            // Konsola acil durum mesajı;
            Console.WriteLine($"[EMERGENCY] LogManager initialization failed: {exception.Message}");

            // Basit bir fallback logger oluştur;
            _logSinks.Add(new ConsoleLogSink());
            MinimumLogLevel = LogLevel.Error;
        }

        private void ValidateDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(LogManager));
        }

        private void ValidateCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                throw new ArgumentException("Log kategorisi boş olamaz", nameof(category));

            if (category.Length > 256)
                throw new ArgumentException("Log kategorisi 256 karakterden uzun olamaz", nameof(category));
        }

        #endregion

        #region IDisposable Implementation

        public void Dispose()
        {
            if (_isDisposed) return;

            try
            {
                _cancellationTokenSource.Cancel();

                // Arka plan görevinin tamamlanmasını bekle;
                _backgroundProcessingTask?.Wait(TimeSpan.FromSeconds(5));

                // Kalan logları işle;
                ProcessRemainingLogs();

                // Sink'leri temizle;
                foreach (var sink in _logSinks.OfType<IDisposable>())
                {
                    sink.Dispose();
                }

                _cancellationTokenSource.Dispose();
                _processingLock.Dispose();
            }
            finally
            {
                _isDisposed = true;
                GC.SuppressFinalize(this);
            }
        }

        private void ProcessRemainingLogs()
        {
            const int maxAttempts = 3;
            const int batchSize = 100;
            int attempt = 0;

            while (_logQueue.Any() && attempt < maxAttempts)
            {
                try
                {
                    var logs = new List<LogEntry>();
                    for (int i = 0; i < batchSize && _logQueue.TryDequeue(out var log); i++)
                    {
                        logs.Add(log);
                    }

                    foreach (var log in logs)
                    {
                        // Sadece kritik logları yaz;
                        if (log.Level >= LogLevel.Error)
                        {
                            foreach (var sink in _logSinks.Where(s => s.SupportsImmediateWrite))
                            {
                                sink.WriteImmediateAsync(log).Wait(TimeSpan.FromSeconds(1));
                            }
                        }
                    }
                }
                catch
                {
                    // Sonlandırma sırasındaki hataları görmezden gel;
                }

                attempt++;
            }
        }

        ~LogManager()
        {
            Dispose();
        }

        #endregion
    }

    #region Supporting Types and Interfaces

    public interface ILogManager
    {
        ILogger GetLogger(string category);
        ILogger<T> GetLogger<T>() where T : class;
        void WriteLog(LogLevel level, string message, string category,
            Exception exception = null, Dictionary<string, object> properties = null);
        Task<IEnumerable<LogEntry>> GetLogsAsync(LogFilter filter);
        void SetMinimumLogLevel(LogLevel level);
        Task ClearLogsAsync();
        LogManagerStatus GetStatus();
        void PauseProcessing();
        void ResumeProcessing();
    }

    public class LogEntry
    {
        public Guid Id { get; set; }
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public string Category { get; set; }
        public string Message { get; set; }
        public Exception Exception { get; set; }
        public Dictionary<string, object> Properties { get; set; }
        public int ThreadId { get; set; }
        public int ProcessId { get; set; }
        public string MachineName { get; set; } = Environment.MachineName;
    }

    public class LogFilter
    {
        public LogLevel? MinimumLevel { get; set; }
        public LogLevel? MaximumLevel { get; set; }
        public string CategoryContains { get; set; }
        public string MessageContains { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public int? MaxResults { get; set; }
    }

    public class LogEventArgs : EventArgs
    {
        public LogEntry LogEntry { get; set; }
        public int SuccessfulSinks { get; set; }
        public List<string> FailedSinks { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class LogManagerStatus
    {
        public bool IsInitialized { get; set; }
        public bool IsProcessingEnabled { get; set; }
        public int ActiveLoggersCount { get; set; }
        public int QueuedLogsCount { get; set; }
        public int ActiveSinksCount { get; set; }
        public LogLevel MinimumLogLevel { get; set; }
        public TaskStatus BackgroundTaskStatus { get; set; }
    }

    public class LogManagerInitializationException : Exception
    {
        public LogManagerInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    internal class LogProcessingFailureContext
    {
        public Exception Exception { get; }
        public DateTime FailureTime { get; }

        public LogProcessingFailureContext(Exception exception)
        {
            Exception = exception;
            FailureTime = DateTime.UtcNow;
        }
    }

    internal class LogSinkFailureContext
    {
        public List<string> FailedSinks { get; }
        public LogEntry FailedLogEntry { get; }
        public DateTime FailureTime { get; }

        public LogSinkFailureContext(List<string> failedSinks, LogEntry failedLogEntry)
        {
            FailedSinks = failedSinks;
            FailedLogEntry = failedLogEntry;
            FailureTime = DateTime.UtcNow;
        }
    }

    #endregion
}
