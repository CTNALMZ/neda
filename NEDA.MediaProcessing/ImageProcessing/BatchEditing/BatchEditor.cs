using NEDA.AI.ComputerVision;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.MediaProcessing.ImageProcessing.BatchEditing.Interfaces;
using NEDA.MediaProcessing.ImageProcessing.ImageFilters;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.MediaProcessing.ImageProcessing.BatchEditing;
{
    /// <summary>
    /// Toplu görüntü düzenleme işlemlerini yöneten ana sınıf;
    /// </summary>
    public class BatchEditor : IBatchEditor, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IImageProcessor _imageProcessor;
        private readonly IFilterEngine _filterEngine;
        private readonly ConcurrentQueue<BatchJob> _jobQueue;
        private readonly List<BatchJob> _completedJobs;
        private readonly object _lockObject = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private bool _isProcessing;
        private int _currentProgress;
        private int _totalJobs;

        #endregion;

        #region Events;

        /// <summary>
        /// Toplu işlem ilerlemesi değiştiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<BatchProgressEventArgs> ProgressChanged;

        /// <summary>
        /// Toplu işlem tamamlandığında tetiklenen event;
        /// </summary>
        public event EventHandler<BatchCompletedEventArgs> BatchCompleted;

        /// <summary>
        /// Toplu işlem başlatıldığında tetiklenen event;
        /// </summary>
        public event EventHandler<BatchStartedEventArgs> BatchStarted;

        /// <summary>
        /// Bir iş başarısız olduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<JobFailedEventArgs> JobFailed;

        #endregion;

        #region Properties;

        /// <summary>
        /// Toplu işlem durumu;
        /// </summary>
        public BatchStatus Status { get; private set; }

        /// <summary>
        /// Mevcut ilerleme yüzdesi (0-100)
        /// </summary>
        public int ProgressPercentage => _totalJobs > 0 ? (int)((_currentProgress * 100.0) / _totalJobs) : 0;

        /// <summary>
        /// Tamamlanan iş sayısı;
        /// </summary>
        public int CompletedJobs => _completedJobs.Count;

        /// <summary>
        /// Kuyruktaki iş sayısı;
        /// </summary>
        public int QueuedJobs => _jobQueue.Count;

        /// <summary>
        /// Toplam iş sayısı;
        /// </summary>
        public int TotalJobs => _totalJobs;

        /// <summary>
        /// İşlem başlatılabilir mi?
        /// </summary>
        public bool CanStart => !_isProcessing && _jobQueue.Count > 0;

        /// <summary>
        /// İşlem durdurulabilir mi?
        /// </summary>
        public bool CanStop => _isProcessing;

        /// <summary>
        /// Varsayılan çıkış dizini;
        /// </summary>
        public string DefaultOutputDirectory { get; set; }

        /// <summary>
        /// Maksimum paralel iş sayısı;
        /// </summary>
        public int MaxParallelJobs { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Çıkış dosya formatı;
        /// </summary>
        public ImageFormat OutputFormat { get; set; } = ImageFormat.Jpeg;

        /// <summary>
        /// JPEG kalite seviyesi (1-100)
        /// </summary>
        public int JpegQuality { get; set; } = 90;

        /// <summary>
        /// PNG sıkıştırma seviyesi (0-9)
        /// </summary>
        public int PngCompressionLevel { get; set; } = 6;

        /// <summary>
        /// Kaynak dosyaların yedeğini al;
        /// </summary>
        public bool BackupSourceFiles { get; set; } = true;

        /// <summary>
        /// Çakışma durumunda üzerine yaz;
        /// </summary>
        public bool OverwriteExisting { get; set; } = false;

        #endregion;

        #region Constructor;

        /// <summary>
        /// BatchEditor sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="errorHandler">Hata yönetimi servisi</param>
        /// <param name="imageProcessor">Görüntü işleme servisi</param>
        /// <param name="filterEngine">Filtre motoru</param>
        public BatchEditor(
            ILogger logger,
            IErrorHandler errorHandler,
            IImageProcessor imageProcessor,
            IFilterEngine filterEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _imageProcessor = imageProcessor ?? throw new ArgumentNullException(nameof(imageProcessor));
            _filterEngine = filterEngine ?? throw new ArgumentNullException(nameof(filterEngine));

            _jobQueue = new ConcurrentQueue<BatchJob>();
            _completedJobs = new List<BatchJob>();
            _cancellationTokenSource = new CancellationTokenSource();

            Status = BatchStatus.Idle;
            DefaultOutputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BatchProcessed");

            _logger.Info("BatchEditor initialized", GetType().Name);
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Toplu iş kuyruğuna iş ekler;
        /// </summary>
        /// <param name="job">Eklenecek iş</param>
        public void AddJob(BatchJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));

            lock (_lockObject)
            {
                if (_isProcessing)
                {
                    throw new InvalidOperationException("Cannot add jobs while processing is in progress");
                }

                _jobQueue.Enqueue(job);
                _totalJobs = _jobQueue.Count;

                _logger.Debug($"Job added to queue: {job.SourcePath}", GetType().Name);
            }
        }

        /// <summary>
        /// Toplu iş kuyruğuna birden fazla iş ekler;
        /// </summary>
        /// <param name="jobs">Eklenecek iş listesi</param>
        public void AddJobs(IEnumerable<BatchJob> jobs)
        {
            if (jobs == null) throw new ArgumentNullException(nameof(jobs));

            lock (_lockObject)
            {
                if (_isProcessing)
                {
                    throw new InvalidOperationException("Cannot add jobs while processing is in progress");
                }

                foreach (var job in jobs)
                {
                    _jobQueue.Enqueue(job);
                }

                _totalJobs = _jobQueue.Count;
                _logger.Info($"{jobs.Count()} jobs added to queue", GetType().Name);
            }
        }

        /// <summary>
        /// Belirtilen desene uyan dosyaları iş kuyruğuna ekler;
        /// </summary>
        /// <param name="directory">Dizin yolu</param>
        /// <param name="searchPattern">Arama deseni (örn: "*.jpg")</param>
        /// <param name="searchOption">Arama seçeneği</param>
        /// <param name="operations">Uygulanacak işlemler</param>
        /// <returns>Eklenen dosya sayısı</returns>
        public int AddFilesByPattern(string directory, string searchPattern,
            SearchOption searchOption = SearchOption.TopDirectoryOnly,
            params BatchOperation[] operations)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Directory cannot be null or empty", nameof(directory));

            if (string.IsNullOrWhiteSpace(searchPattern))
                throw new ArgumentException("Search pattern cannot be null or empty", nameof(searchPattern));

            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Directory not found: {directory}");

            var files = Directory.GetFiles(directory, searchPattern, searchOption);
            var addedCount = 0;

            foreach (var file in files)
            {
                try
                {
                    var job = new BatchJob;
                    {
                        SourcePath = file,
                        Operations = operations.ToList(),
                        OutputPath = GenerateOutputPath(file)
                    };

                    AddJob(job);
                    addedCount++;
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to add file {file}: {ex.Message}", GetType().Name);
                }
            }

            _logger.Info($"Added {addedCount} files from {directory} with pattern {searchPattern}", GetType().Name);
            return addedCount;
        }

        /// <summary>
        /// Toplu işlemi başlatır;
        /// </summary>
        /// <returns>İşlem tamamlanana kadar bekleyen Task</returns>
        public async Task StartProcessingAsync()
        {
            lock (_lockObject)
            {
                if (_isProcessing)
                {
                    throw new InvalidOperationException("Processing is already in progress");
                }

                if (_jobQueue.IsEmpty)
                {
                    throw new InvalidOperationException("No jobs in queue");
                }

                _isProcessing = true;
                Status = BatchStatus.Processing;
                _currentProgress = 0;
                _completedJobs.Clear();
                _cancellationTokenSource = new CancellationTokenSource();
            }

            try
            {
                OnBatchStarted(new BatchStartedEventArgs(_totalJobs));

                _logger.Info($"Starting batch processing of {_totalJobs} jobs", GetType().Name);

                // Paralel işlem için semafor;
                var semaphore = new SemaphoreSlim(MaxParallelJobs);
                var tasks = new List<Task>();

                while (!_jobQueue.IsEmpty && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    if (_jobQueue.TryDequeue(out var job))
                    {
                        await semaphore.WaitAsync(_cancellationTokenSource.Token);

                        var task = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessJobAsync(job, _cancellationTokenSource.Token);
                                lock (_lockObject)
                                {
                                    _completedJobs.Add(job);
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                _logger.Info($"Job cancelled: {job.SourcePath}", GetType().Name);
                            }
                            catch (Exception ex)
                            {
                                OnJobFailed(new JobFailedEventArgs(job, ex));
                                _logger.Error($"Job failed: {job.SourcePath} - {ex.Message}", GetType().Name);
                            }
                            finally
                            {
                                semaphore.Release();
                                UpdateProgress();
                            }
                        }, _cancellationTokenSource.Token);

                        tasks.Add(task);
                    }
                }

                await Task.WhenAll(tasks);

                if (_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    Status = BatchStatus.Cancelled;
                    _logger.Info("Batch processing cancelled", GetType().Name);
                }
                else;
                {
                    Status = BatchStatus.Completed;
                    _logger.Info("Batch processing completed successfully", GetType().Name);
                }
            }
            catch (Exception ex)
            {
                Status = BatchStatus.Failed;
                _logger.Error($"Batch processing failed: {ex.Message}", GetType().Name);
                throw;
            }
            finally
            {
                lock (_lockObject)
                {
                    _isProcessing = false;
                }

                OnBatchCompleted(new BatchCompletedEventArgs(
                    Status,
                    _completedJobs.Count,
                    _totalJobs,
                    DateTime.Now;
                ));
            }
        }

        /// <summary>
        /// Toplu işlemi durdurur;
        /// </summary>
        public void StopProcessing()
        {
            lock (_lockObject)
            {
                if (!_isProcessing)
                {
                    return;
                }

                _cancellationTokenSource.Cancel();
                Status = BatchStatus.Cancelling;
                _logger.Info("Stopping batch processing...", GetType().Name);
            }
        }

        /// <summary>
        /// Tüm iş kuyruğunu temizler;
        /// </summary>
        public void ClearQueue()
        {
            lock (_lockObject)
            {
                if (_isProcessing)
                {
                    throw new InvalidOperationException("Cannot clear queue while processing");
                }

                while (_jobQueue.TryDequeue(out _)) { }
                _totalJobs = 0;
                _currentProgress = 0;
                _completedJobs.Clear();

                _logger.Info("Job queue cleared", GetType().Name);
            }
        }

        /// <summary>
        /// Belirtilen dizindeki tüm görüntülere filtre uygular;
        /// </summary>
        /// <param name="directory">Dizin yolu</param>
        /// <param name="filter">Uygulanacak filtre</param>
        /// <returns>İşlenen dosya sayısı</returns>
        public async Task<int> ApplyFilterToDirectoryAsync(string directory, ImageFilter filter)
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new ArgumentException("Directory cannot be null or empty", nameof(directory));

            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff" };
            var operations = new[] { new BatchOperation { Filter = filter } };

            ClearQueue();

            foreach (var extension in supportedExtensions)
            {
                AddFilesByPattern(directory, $"*{extension}",
                    SearchOption.TopDirectoryOnly, operations);
            }

            if (TotalJobs == 0)
            {
                _logger.Warning($"No image files found in directory: {directory}", GetType().Name);
                return 0;
            }

            await StartProcessingAsync();
            return CompletedJobs;
        }

        /// <summary>
        /// İş kuyruğunu JSON formatında dışa aktarır;
        /// </summary>
        /// <param name="filePath">Kaydedilecek dosya yolu</param>
        public async Task ExportQueueAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            lock (_lockObject)
            {
                if (_isProcessing)
                {
                    throw new InvalidOperationException("Cannot export queue while processing");
                }
            }

            var queueData = new BatchQueueData;
            {
                Jobs = _jobQueue.ToList(),
                OutputDirectory = DefaultOutputDirectory,
                OutputFormat = OutputFormat,
                JpegQuality = JpegQuality,
                PngCompressionLevel = PngCompressionLevel,
                CreatedDate = DateTime.Now;
            };

            var json = System.Text.Json.JsonSerializer.Serialize(queueData,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(filePath, json);
            _logger.Info($"Queue exported to {filePath}", GetType().Name);
        }

        /// <summary>
        /// JSON formatındaki iş kuyruğunu içe aktarır;
        /// </summary>
        /// <param name="filePath">İçe aktarılacak dosya yolu</param>
        public async Task ImportQueueAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"Queue file not found: {filePath}");

            lock (_lockObject)
            {
                if (_isProcessing)
                {
                    throw new InvalidOperationException("Cannot import queue while processing");
                }
            }

            var json = await File.ReadAllTextAsync(filePath);
            var queueData = System.Text.Json.JsonSerializer.Deserialize<BatchQueueData>(json);

            if (queueData == null)
                throw new InvalidDataException("Failed to deserialize queue data");

            ClearQueue();

            // Ayarları güncelle;
            DefaultOutputDirectory = queueData.OutputDirectory ?? DefaultOutputDirectory;
            OutputFormat = queueData.OutputFormat;
            JpegQuality = queueData.JpegQuality;
            PngCompressionLevel = queueData.PngCompressionLevel;

            // İşleri ekle;
            AddJobs(queueData.Jobs);

            _logger.Info($"Queue imported from {filePath} with {TotalJobs} jobs", GetType().Name);
        }

        /// <summary>
        /// Mevcut kuyruk durumunu alır;
        /// </summary>
        /// <returns>Kuyruk durumu raporu</returns>
        public BatchQueueStatus GetQueueStatus()
        {
            lock (_lockObject)
            {
                return new BatchQueueStatus;
                {
                    TotalJobs = _totalJobs,
                    QueuedJobs = QueuedJobs,
                    CompletedJobs = CompletedJobs,
                    IsProcessing = _isProcessing,
                    Status = Status,
                    ProgressPercentage = ProgressPercentage,
                    EstimatedCompletionTime = CalculateEstimatedCompletionTime()
                };
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Tek bir işi işler;
        /// </summary>
        private async Task ProcessJobAsync(BatchJob job, CancellationToken cancellationToken)
        {
            _logger.Debug($"Processing job: {job.SourcePath}", GetType().Name);

            // Dosya varlığını kontrol et;
            if (!File.Exists(job.SourcePath))
            {
                throw new FileNotFoundException($"Source file not found: {job.SourcePath}");
            }

            // Çıkış dizinini oluştur;
            var outputDirectory = Path.GetDirectoryName(job.OutputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory) && !Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // Kaynak dosyanın yedeğini al;
            if (BackupSourceFiles)
            {
                await BackupSourceFileAsync(job.SourcePath, cancellationToken);
            }

            // Görüntüyü yükle;
            using var image = await _imageProcessor.LoadImageAsync(job.SourcePath, cancellationToken);

            // İşlemleri uygula;
            foreach (var operation in job.Operations)
            {
                await ApplyOperationAsync(image, operation, cancellationToken);
            }

            // Çıkış formatına göre kaydet;
            await SaveImageWithFormatAsync(image, job.OutputPath, cancellationToken);

            _logger.Debug($"Job completed: {job.SourcePath} -> {job.OutputPath}", GetType().Name);
        }

        /// <summary>
        /// Kaynak dosyanın yedeğini alır;
        /// </summary>
        private async Task BackupSourceFileAsync(string sourcePath, CancellationToken cancellationToken)
        {
            var backupDir = Path.Combine(DefaultOutputDirectory, "Backups");
            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            var backupPath = Path.Combine(backupDir,
                $"{Path.GetFileNameWithoutExtension(sourcePath)}_backup{Path.GetExtension(sourcePath)}");

            await Task.Run(() => File.Copy(sourcePath, backupPath, true), cancellationToken);
        }

        /// <summary>
        /// İşlemi görüntüye uygular;
        /// </summary>
        private async Task ApplyOperationAsync(ImageData image, BatchOperation operation, CancellationToken cancellationToken)
        {
            if (operation.Filter != null)
            {
                await _filterEngine.ApplyFilterAsync(image, operation.Filter, cancellationToken);
            }

            if (operation.ResizeWidth > 0 || operation.ResizeHeight > 0)
            {
                await _imageProcessor.ResizeAsync(image,
                    operation.ResizeWidth,
                    operation.ResizeHeight,
                    operation.MaintainAspectRatio,
                    cancellationToken);
            }

            if (operation.CropRectangle != null)
            {
                await _imageProcessor.CropAsync(image, operation.CropRectangle, cancellationToken);
            }

            if (operation.RotationAngle != 0)
            {
                await _imageProcessor.RotateAsync(image, operation.RotationAngle, cancellationToken);
            }

            if (operation.Adjustments != null)
            {
                await _imageProcessor.AdjustColorsAsync(image, operation.Adjustments, cancellationToken);
            }
        }

        /// <summary>
        /// Görüntüyü belirtilen formatta kaydeder;
        /// </summary>
        private async Task SaveImageWithFormatAsync(ImageData image, string outputPath, CancellationToken cancellationToken)
        {
            // Çakışma kontrolü;
            if (File.Exists(outputPath) && !OverwriteExisting)
            {
                var fileName = Path.GetFileNameWithoutExtension(outputPath);
                var extension = Path.GetExtension(outputPath);
                var directory = Path.GetDirectoryName(outputPath);
                var counter = 1;

                do;
                {
                    outputPath = Path.Combine(directory, $"{fileName}_{counter}{extension}");
                    counter++;
                } while (File.Exists(outputPath));
            }

            // Format seçenekleri;
            var saveOptions = new ImageSaveOptions;
            {
                Format = OutputFormat,
                JpegQuality = JpegQuality,
                PngCompressionLevel = PngCompressionLevel,
                Overwrite = OverwriteExisting;
            };

            await _imageProcessor.SaveImageAsync(image, outputPath, saveOptions, cancellationToken);
        }

        /// <summary>
        /// Çıkış dosya yolu oluşturur;
        /// </summary>
        private string GenerateOutputPath(string sourcePath)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var extension = OutputFormat switch;
            {
                ImageFormat.Jpeg => ".jpg",
                ImageFormat.Png => ".png",
                ImageFormat.Bmp => ".bmp",
                ImageFormat.Gif => ".gif",
                ImageFormat.Tiff => ".tiff",
                _ => ".jpg"
            };

            var outputFileName = $"{fileName}_processed{extension}";
            return Path.Combine(DefaultOutputDirectory, outputFileName);
        }

        /// <summary>
        /// İlerlemeyi günceller ve event tetikler;
        /// </summary>
        private void UpdateProgress()
        {
            lock (_lockObject)
            {
                _currentProgress = _completedJobs.Count;
            }

            OnProgressChanged(new BatchProgressEventArgs(
                ProgressPercentage,
                _currentProgress,
                _totalJobs,
                _jobQueue.Count;
            ));
        }

        /// <summary>
        /// Tahmini tamamlanma süresini hesaplar;
        /// </summary>
        private DateTime? CalculateEstimatedCompletionTime()
        {
            if (!_isProcessing || _currentProgress == 0 || _totalJobs == 0)
                return null;

            var elapsedTime = DateTime.Now - _batchStartTime;
            var timePerJob = elapsedTime.TotalSeconds / _currentProgress;
            var remainingJobs = _totalJobs - _currentProgress;
            var remainingTime = TimeSpan.FromSeconds(timePerJob * remainingJobs);

            return DateTime.Now.Add(remainingTime);
        }

        private DateTime _batchStartTime;

        #endregion;

        #region Event Invokers;

        /// <summary>
        /// İlerleme değişti event'ini tetikler;
        /// </summary>
        protected virtual void OnProgressChanged(BatchProgressEventArgs e)
        {
            ProgressChanged?.Invoke(this, e);
        }

        /// <summary>
        /// Toplu işlem tamamlandı event'ini tetikler;
        /// </summary>
        protected virtual void OnBatchCompleted(BatchCompletedEventArgs e)
        {
            BatchCompleted?.Invoke(this, e);
        }

        /// <summary>
        /// Toplu işlem başladı event'ini tetikler;
        /// </summary>
        protected virtual void OnBatchStarted(BatchStartedEventArgs e)
        {
            _batchStartTime = DateTime.Now;
            BatchStarted?.Invoke(this, e);
        }

        /// <summary>
        /// İş başarısız oldu event'ini tetikler;
        /// </summary>
        protected virtual void OnJobFailed(JobFailedEventArgs e)
        {
            JobFailed?.Invoke(this, e);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cancellationTokenSource?.Dispose();
                    StopProcessing();

                    lock (_lockObject)
                    {
                        _jobQueue.Clear();
                        _completedJobs.Clear();
                    }

                    _logger.Info("BatchEditor disposed", GetType().Name);
                }

                _disposed = true;
            }
        }

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~BatchEditor()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// Toplu iş durumu;
    /// </summary>
    public enum BatchStatus;
    {
        Idle,
        Processing,
        Paused,
        Cancelling,
        Cancelled,
        Completed,
        Failed;
    }

    /// <summary>
    /// Görüntü formatı;
    /// </summary>
    public enum ImageFormat;
    {
        Jpeg,
        Png,
        Bmp,
        Gif,
        Tiff,
        WebP;
    }

    /// <summary>
    /// Toplu iş işi;
    /// </summary>
    public class BatchJob;
    {
        public string SourcePath { get; set; }
        public string OutputPath { get; set; }
        public List<BatchOperation> Operations { get; set; } = new List<BatchOperation>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DateTime AddedDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Toplu iş operasyonu;
    /// </summary>
    public class BatchOperation;
    {
        public ImageFilter Filter { get; set; }
        public int ResizeWidth { get; set; }
        public int ResizeHeight { get; set; }
        public bool MaintainAspectRatio { get; set; } = true;
        public Rectangle CropRectangle { get; set; }
        public float RotationAngle { get; set; }
        public ColorAdjustments Adjustments { get; set; }
        public string WatermarkText { get; set; }
        public string WatermarkImagePath { get; set; }
    }

    /// <summary>
    /// Toplu iş kuyruğu verisi;
    /// </summary>
    public class BatchQueueData;
    {
        public List<BatchJob> Jobs { get; set; } = new List<BatchJob>();
        public string OutputDirectory { get; set; }
        public ImageFormat OutputFormat { get; set; }
        public int JpegQuality { get; set; }
        public int PngCompressionLevel { get; set; }
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>
    /// Kuyruk durumu;
    /// </summary>
    public class BatchQueueStatus;
    {
        public int TotalJobs { get; set; }
        public int QueuedJobs { get; set; }
        public int CompletedJobs { get; set; }
        public bool IsProcessing { get; set; }
        public BatchStatus Status { get; set; }
        public int ProgressPercentage { get; set; }
        public DateTime? EstimatedCompletionTime { get; set; }
        public TimeSpan? EstimatedRemainingTime => EstimatedCompletionTime.HasValue;
            ? EstimatedCompletionTime.Value - DateTime.Now;
            : null;
    }

    /// <summary>
    /// İlerleme event argümanları;
    /// </summary>
    public class BatchProgressEventArgs : EventArgs;
    {
        public int ProgressPercentage { get; }
        public int ProcessedJobs { get; }
        public int TotalJobs { get; }
        public int RemainingJobs { get; }

        public BatchProgressEventArgs(int progressPercentage, int processedJobs, int totalJobs, int remainingJobs)
        {
            ProgressPercentage = progressPercentage;
            ProcessedJobs = processedJobs;
            TotalJobs = totalJobs;
            RemainingJobs = remainingJobs;
        }
    }

    /// <summary>
    /// Tamamlanma event argümanları;
    /// </summary>
    public class BatchCompletedEventArgs : EventArgs;
    {
        public BatchStatus Status { get; }
        public int ProcessedJobs { get; }
        public int TotalJobs { get; }
        public DateTime CompletionTime { get; }
        public bool IsSuccess => Status == BatchStatus.Completed;

        public BatchCompletedEventArgs(BatchStatus status, int processedJobs, int totalJobs, DateTime completionTime)
        {
            Status = status;
            ProcessedJobs = processedJobs;
            TotalJobs = totalJobs;
            CompletionTime = completionTime;
        }
    }

    /// <summary>
    /// Başlangıç event argümanları;
    /// </summary>
    public class BatchStartedEventArgs : EventArgs;
    {
        public int TotalJobs { get; }
        public DateTime StartTime { get; } = DateTime.Now;

        public BatchStartedEventArgs(int totalJobs)
        {
            TotalJobs = totalJobs;
        }
    }

    /// <summary>
    /// İş başarısız event argümanları;
    /// </summary>
    public class JobFailedEventArgs : EventArgs;
    {
        public BatchJob Job { get; }
        public Exception Exception { get; }

        public JobFailedEventArgs(BatchJob job, Exception exception)
        {
            Job = job;
            Exception = exception;
        }
    }

    /// <summary>
    /// Görüntü kaydetme seçenekleri;
    /// </summary>
    public class ImageSaveOptions;
    {
        public ImageFormat Format { get; set; }
        public int JpegQuality { get; set; } = 90;
        public int PngCompressionLevel { get; set; } = 6;
        public bool Overwrite { get; set; }
    }

    /// <summary>
    /// Renk ayarlamaları;
    /// </summary>
    public class ColorAdjustments;
    {
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Saturation { get; set; }
        public float Hue { get; set; }
        public float Gamma { get; set; }
    }

    /// <summary>
    /// Dikdörtgen yapısı;
    /// </summary>
    public class Rectangle;
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }

    /// <summary>
    /// Görüntü veri yapısı;
    /// </summary>
    public class ImageData : IDisposable
    {
        public byte[] Data { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public ImageFormat Format { get; set; }

        public void Dispose()
        {
            Data = null;
        }
    }

    #endregion;
}
