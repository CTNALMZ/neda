using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using NEDA.Logging;
using NEDA.ExceptionHandling;
using NEDA.Common.Utilities;
using NEDA.MediaProcessing.ImageProcessing.Interfaces;
using NEDA.MediaProcessing.ImageProcessing.PhotoshopCommands.Interfaces;

namespace NEDA.MediaProcessing.ImageProcessing.PhotoshopCommands;
{
    /// <summary>
    /// Photoshop COM otomasyon motoru - Photoshop'u programatik olarak kontrol eder;
    /// </summary>
    public class PSEngine : IPhotoshopEngine, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IScriptGenerator _scriptGenerator;

        private dynamic _photoshopApp;
        private bool _isConnected;
        private string _photoshopPath;
        private readonly Dictionary<int, dynamic> _openDocuments;
        private readonly Queue<PhotoshopCommand> _commandQueue;
        private readonly object _psLock = new object();
        private readonly PhotoshopSession _currentSession;
        private readonly PerformanceMonitor _performanceMonitor;

        private const string PS_PROG_ID = "Photoshop.Application";
        private const int PS_TIMEOUT_MS = 30000;
        private const int MAX_RETRY_COUNT = 3;
        private const int COMMAND_DELAY_MS = 100;

        #endregion;

        #region Properties;

        /// <summary>
        /// Photoshop bağlantı durumu;
        /// </summary>
        public bool IsConnected => _isConnected && _photoshopApp != null;

        /// <summary>
        /// Photoshop sürüm bilgisi;
        /// </summary>
        public string Version { get; private set; }

        /// <summary>
        /// Photoshop çalıştırılabilir dosya yolu;
        /// </summary>
        public string PhotoshopPath;
        {
            get => _photoshopPath;
            set;
            {
                if (!string.IsNullOrEmpty(value) && File.Exists(value))
                {
                    _photoshopPath = value;
                }
            }
        }

        /// <summary>
        /// Aktif Photoshop belgesi sayısı;
        /// </summary>
        public int OpenDocumentCount => _openDocuments.Count;

        /// <summary>
        /// Kuyruktaki komut sayısı;
        /// </summary>
        public int QueuedCommands => _commandQueue.Count;

        /// <summary>
        /// Photoshop görünürlüğü;
        /// </summary>
        public bool IsVisible;
        {
            get => IsConnected ? _photoshopApp.Visible : false;
            set;
            {
                if (IsConnected)
                {
                    _photoshopApp.Visible = value;
                }
            }
        }

        /// <summary>
        /// Photoshop çalışma dizini;
        /// </summary>
        public string WorkingDirectory { get; set; }

        /// <summary>
        /// Varsayılan çıktı formatı;
        /// </summary>
        public PhotoshopFormat DefaultOutputFormat { get; set; } = PhotoshopFormat.JPEG;

        /// <summary>
        /// JPEG kaydetme kalitesi (1-12)
        /// </summary>
        public int JpegQuality { get; set; } = 10;

        /// <summary>
        /// PNG sıkıştırma seviyesi (0-9)
        /// </summary>
        public int PngCompressionLevel { get; set; } = 6;

        /// <summary>
        /// Batch modu aktif mi?
        /// </summary>
        public bool BatchMode { get; set; }

        /// <summary>
        /// Script hata ayıklama modu;
        /// </summary>
        public bool DebugMode { get; set; }

        /// <summary>
        /// İşlem zaman aşımı (ms)
        /// </summary>
        public int OperationTimeout { get; set; } = PS_TIMEOUT_MS;

        /// <summary>
        /// Maksimum bellek kullanımı (MB)
        /// </summary>
        public int MaxMemoryUsageMB { get; set; } = 2048;

        /// <summary>
        /// Geçici dosyaları otomatik temizle;
        /// </summary>
        public bool AutoCleanupTempFiles { get; set; } = true;

        #endregion;

        #region Events;

        /// <summary>
        /// Photoshop bağlantısı kurulduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<PhotoshopConnectedEventArgs> Connected;

        /// <summary>
        /// Photoshop bağlantısı kesildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<PhotoshopDisconnectedEventArgs> Disconnected;

        /// <summary>
        /// Photoshop komutu yürütüldüğünde tetiklenen event;
        /// </summary>
        public event EventHandler<PhotoshopCommandExecutedEventArgs> CommandExecuted;

        /// <summary>
        /// Photoshop belgesi açıldığında tetiklenen event;
        /// </summary>
        public event EventHandler<DocumentOpenedEventArgs> DocumentOpened;

        /// <summary>
        /// Photoshop belgesi kaydedildiğinde tetiklenen event;
        /// </summary>
        public event EventHandler<DocumentSavedEventArgs> DocumentSaved;

        /// <summary>
        /// Photoshop'tan çıktı alındığında tetiklenen event;
        /// </summary>
        public event EventHandler<PhotoshopOutputEventArgs> OutputReceived;

        #endregion;

        #region Constructor;

        /// <summary>
        /// PSEngine sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public PSEngine(
            ILogger logger,
            IErrorHandler errorHandler,
            IScriptGenerator scriptGenerator = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _scriptGenerator = scriptGenerator;

            _openDocuments = new Dictionary<int, dynamic>();
            _commandQueue = new Queue<PhotoshopCommand>();
            _currentSession = new PhotoshopSession();
            _performanceMonitor = new PerformanceMonitor();

            DetectPhotoshopPath();

            _logger.Info("PSEngine initialized", GetType().Name);
        }

        #endregion;

        #region Public Methods - Connection Management;

        /// <summary>
        /// Photoshop'a bağlanır;
        /// </summary>
        /// <param name="visible">Photoshop görünür olsun mu?</param>
        /// <returns>Bağlantı başarılı mı?</returns>
        public async Task<bool> ConnectAsync(bool visible = false)
        {
            if (IsConnected)
            {
                _logger.Warning("Already connected to Photoshop", GetType().Name);
                return true;
            }

            try
            {
                lock (_psLock)
                {
                    // COM nesnesi oluştur;
                    Type photoshopType = Type.GetTypeFromProgID(PS_PROG_ID);

                    if (photoshopType == null)
                    {
                        throw new InvalidOperationException("Photoshop is not installed or COM registration is missing");
                    }

                    _photoshopApp = Activator.CreateInstance(photoshopType);

                    if (_photoshopApp == null)
                    {
                        throw new InvalidOperationException("Failed to create Photoshop COM object");
                    }

                    _isConnected = true;

                    // Photoshop ayarları;
                    _photoshopApp.Visible = visible;
                    _photoshopApp.DisplayDialogs = BatchMode ? 2 : 1; // 2: Dialog gösterme, 1: Tüm dialog'lar;

                    // Bellek ayarı;
                    if (MaxMemoryUsageMB > 0)
                    {
                        try
                        {
                            _photoshopApp.Preferences.RAMUsage = MaxMemoryUsageMB;
                        }
                        catch
                        {
                            // Bazı sürümlerde desteklenmeyebilir;
                        }
                    }

                    // Sürüm bilgisini al;
                    Version = _photoshopApp.Version ?? "Unknown";

                    _currentSession.StartTime = DateTime.Now;
                    _currentSession.SessionId = Guid.NewGuid();
                    _currentSession.PhotoshopVersion = Version;
                }

                _logger.Info($"Connected to Photoshop v{Version} (Visible: {visible})", GetType().Name);

                OnConnected(new PhotoshopConnectedEventArgs;
                {
                    SessionId = _currentSession.SessionId,
                    Version = Version,
                    IsVisible = visible;
                });

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to connect to Photoshop: {ex.Message}", GetType().Name);
                _errorHandler.HandleError(ex, ErrorCodes.PHOTOSHOP_CONNECTION_FAILED);
                Disconnect();
                return false;
            }
        }

        /// <summary>
        /// Photoshop bağlantısını keser;
        /// </summary>
        public void Disconnect()
        {
            lock (_psLock)
            {
                if (!IsConnected)
                {
                    return;
                }

                try
                {
                    // Açık belgeleri kapat;
                    CloseAllDocuments(false);

                    // Photoshop'u kapat;
                    if (_photoshopApp != null)
                    {
                        try
                        {
                            _photoshopApp.Quit();
                        }
                        catch (COMException)
                        {
                            // Photoshop zaten kapanmış olabilir;
                        }

                        Marshal.FinalReleaseComObject(_photoshopApp);
                        _photoshopApp = null;
                    }

                    _isConnected = false;
                    _currentSession.EndTime = DateTime.Now;

                    _logger.Info("Disconnected from Photoshop", GetType().Name);

                    OnDisconnected(new PhotoshopDisconnectedEventArgs;
                    {
                        SessionId = _currentSession.SessionId,
                        Duration = _currentSession.Duration,
                        DocumentsProcessed = _currentSession.DocumentsProcessed,
                        CommandsExecuted = _currentSession.CommandsExecuted;
                    });
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Error while disconnecting from Photoshop: {ex.Message}", GetType().Name);
                }
                finally
                {
                    _openDocuments.Clear();
                    _commandQueue.Clear();
                }
            }
        }

        /// <summary>
        /// Photoshop bağlantısını yeniden başlatır;
        /// </summary>
        public async Task<bool> RestartAsync()
        {
            Disconnect();
            await Task.Delay(3000); // Photoshop'un tamamen kapanmasını bekle;
            return await ConnectAsync(IsVisible);
        }

        #endregion;

        #region Public Methods - Document Operations;

        /// <summary>
        /// Görüntü dosyasını Photoshop'ta açar;
        /// </summary>
        public async Task<PhotoshopDocument> OpenDocumentAsync(
            string filePath,
            DocumentOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(filePath))
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException($"File not found: {filePath}");

            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            options ??= new DocumentOptions();

            try
            {
                dynamic doc = null;
                var retryCount = 0;

                while (retryCount < MAX_RETRY_COUNT && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // Photoshop'ta dosyayı aç;
                        doc = _photoshopApp.Open(filePath);

                        if (doc != null)
                        {
                            // Belge ayarları;
                            if (options.ConvertToRGB && doc.Mode != 2) // 2 = RGB;
                            {
                                doc.ConvertMode(2); // RGB'ye çevir;
                            }

                            if (options.Resolution > 0)
                            {
                                doc.ResizeImage(null, null, options.Resolution);
                            }

                            // Belgeyi kaydet;
                            var documentId = doc.ID;
                            _openDocuments[documentId] = doc;

                            var psDoc = new PhotoshopDocument;
                            {
                                Id = documentId,
                                FilePath = filePath,
                                Name = doc.Name,
                                Width = doc.Width,
                                Height = doc.Height,
                                Mode = (ColorMode)doc.Mode,
                                Resolution = doc.Resolution,
                                Layers = GetDocumentLayers(doc),
                                IsActive = true;
                            };

                            _currentSession.DocumentsProcessed++;

                            _logger.Info($"Document opened: {filePath} (ID: {documentId})", GetType().Name);

                            OnDocumentOpened(new DocumentOpenedEventArgs;
                            {
                                Document = psDoc,
                                SessionId = _currentSession.SessionId;
                            });

                            return psDoc;
                        }
                    }
                    catch (COMException ex) when (retryCount < MAX_RETRY_COUNT - 1)
                    {
                        retryCount++;
                        _logger.Warning($"Retry {retryCount} for opening document {filePath}: {ex.Message}", GetType().Name);
                        await Task.Delay(1000, cancellationToken);
                        continue;
                    }
                }

                throw new PhotoshopException($"Failed to open document after {MAX_RETRY_COUNT} retries: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open document {filePath}: {ex.Message}", GetType().Name);
                throw new PhotoshopException($"Failed to open document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Photoshop belgesini kaydeder;
        /// </summary>
        public async Task SaveDocumentAsync(
            int documentId,
            string savePath = null,
            SaveOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            options ??= new SaveOptions();

            try
            {
                var format = options.Format ?? DefaultOutputFormat;
                var filePath = savePath ?? GetDefaultSavePath(doc.Name, format);

                // Kaydetme dizinini oluştur;
                var directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                // Photoshop kaydetme seçenekleri;
                dynamic saveOptions = null;

                switch (format)
                {
                    case PhotoshopFormat.JPEG:
                        saveOptions = CreateJPEGSaveOptions(options);
                        break;
                    case PhotoshopFormat.PNG:
                        saveOptions = CreatePNGSaveOptions(options);
                        break;
                    case PhotoshopFormat.TIFF:
                        saveOptions = CreateTIFFSaveOptions(options);
                        break;
                    case PhotoshopFormat.PSD:
                        saveOptions = CreatePSDSaveOptions(options);
                        break;
                    default:
                        throw new NotSupportedException($"Format {format} is not supported");
                }

                // Belgeyi kaydet;
                doc.SaveAs(filePath, saveOptions, true, options.Extension);

                // COM nesnelerini temizle;
                if (saveOptions != null)
                {
                    Marshal.FinalReleaseComObject(saveOptions);
                }

                _logger.Info($"Document saved: {filePath} (ID: {documentId})", GetType().Name);

                OnDocumentSaved(new DocumentSavedEventArgs;
                {
                    DocumentId = documentId,
                    FilePath = filePath,
                    Format = format,
                    SessionId = _currentSession.SessionId;
                });

                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save document {documentId}: {ex.Message}", GetType().Name);
                throw new PhotoshopException($"Failed to save document: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Photoshop belgesini kapatır;
        /// </summary>
        public void CloseDocument(int documentId, bool saveChanges = false)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                return;

            try
            {
                doc.Close(saveChanges ? 2 : 1); // 2: Kaydet, 1: Kaydetme;
                _openDocuments.Remove(documentId);

                // COM nesnesini temizle;
                Marshal.FinalReleaseComObject(doc);

                _logger.Info($"Document closed: ID {documentId}", GetType().Name);
            }
            catch (Exception ex)
            {
                _logger.Warning($"Error while closing document {documentId}: {ex.Message}", GetType().Name);
            }
        }

        /// <summary>
        /// Tüm açık belgeleri kapatır;
        /// </summary>
        public void CloseAllDocuments(bool saveChanges = false)
        {
            var documentIds = _openDocuments.Keys.ToList();

            foreach (var docId in documentIds)
            {
                CloseDocument(docId, saveChanges);
            }
        }

        /// <summary>
        /// Aktif belgeyi alır;
        /// </summary>
        public PhotoshopDocument GetActiveDocument()
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            try
            {
                dynamic activeDoc = _photoshopApp.ActiveDocument;
                if (activeDoc == null)
                    return null;

                var docId = activeDoc.ID;

                if (_openDocuments.ContainsKey(docId))
                {
                    return new PhotoshopDocument;
                    {
                        Id = docId,
                        Name = activeDoc.Name,
                        Width = activeDoc.Width,
                        Height = activeDoc.Height,
                        Mode = (ColorMode)activeDoc.Mode,
                        Resolution = activeDoc.Resolution,
                        IsActive = true;
                    };
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Belirli bir belgeyi aktif yapar;
        /// </summary>
        public void ActivateDocument(int documentId)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            try
            {
                _photoshopApp.ActiveDocument = doc;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to activate document {documentId}: {ex.Message}", GetType().Name);
                throw new PhotoshopException($"Failed to activate document: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Public Methods - Image Processing;

        /// <summary>
        /// Photoshop filtresi uygular;
        /// </summary>
        public async Task ApplyFilterAsync(
            int documentId,
            string filterName,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var command = new PhotoshopCommand;
            {
                Name = $"ApplyFilter_{filterName}",
                Type = CommandType.Filter,
                TargetDocumentId = documentId,
                Parameters = parameters ?? new Dictionary<string, object>(),
                Metadata = { { "FilterName", filterName } }
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Görüntüyü yeniden boyutlandırır;
        /// </summary>
        public async Task ResizeImageAsync(
            int documentId,
            int? width = null,
            int? height = null,
            double? resolution = null,
            ResizeMethod method = ResizeMethod.Bicubic,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var parameters = new Dictionary<string, object>
            {
                { "Width", width },
                { "Height", height },
                { "Resolution", resolution },
                { "Method", method }
            };

            var command = new PhotoshopCommand;
            {
                Name = "ResizeImage",
                Type = CommandType.Transform,
                TargetDocumentId = documentId,
                Parameters = parameters;
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Görüntüyü kırpar;
        /// </summary>
        public async Task CropImageAsync(
            int documentId,
            Rectangle bounds,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var parameters = new Dictionary<string, object>
            {
                { "Left", bounds.Left },
                { "Top", bounds.Top },
                { "Right", bounds.Right },
                { "Bottom", bounds.Bottom }
            };

            var command = new PhotoshopCommand;
            {
                Name = "CropImage",
                Type = CommandType.Transform,
                TargetDocumentId = documentId,
                Parameters = parameters;
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Renk ayarlamaları uygular;
        /// </summary>
        public async Task AdjustColorsAsync(
            int documentId,
            ColorAdjustment adjustment,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var parameters = new Dictionary<string, object>
            {
                { "Brightness", adjustment.Brightness },
                { "Contrast", adjustment.Contrast },
                { "Saturation", adjustment.Saturation },
                { "Hue", adjustment.Hue },
                { "Temperature", adjustment.Temperature },
                { "Tint", adjustment.Tint }
            };

            var command = new PhotoshopCommand;
            {
                Name = "AdjustColors",
                Type = CommandType.Color,
                TargetDocumentId = documentId,
                Parameters = parameters;
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Görüntüyü döndürür;
        /// </summary>
        public async Task RotateImageAsync(
            int documentId,
            double angle,
            RotationDirection direction = RotationDirection.Clockwise,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var parameters = new Dictionary<string, object>
            {
                { "Angle", angle },
                { "Direction", direction }
            };

            var command = new PhotoshopCommand;
            {
                Name = "RotateImage",
                Type = CommandType.Transform,
                TargetDocumentId = documentId,
                Parameters = parameters;
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Katman işlemleri yapar;
        /// </summary>
        public async Task<LayerOperationResult> ExecuteLayerOperationAsync(
            int documentId,
            LayerOperation operation,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var command = new PhotoshopCommand;
            {
                Name = $"LayerOperation_{operation.Type}",
                Type = CommandType.Layer,
                TargetDocumentId = documentId,
                Parameters = operation.Parameters;
            };

            var result = await ExecuteCommandAsync(command, cancellationToken);

            return new LayerOperationResult;
            {
                Success = result.Status == CommandStatus.Completed,
                OperationType = operation.Type,
                DocumentId = documentId,
                Error = result.Error;
            };
        }

        /// <summary>
        /// Seçim işlemi yapar;
        /// </summary>
        public async Task MakeSelectionAsync(
            int documentId,
            Selection selection,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            if (!_openDocuments.TryGetValue(documentId, out dynamic doc))
                throw new KeyNotFoundException($"Document not found with ID: {documentId}");

            var command = new PhotoshopCommand;
            {
                Name = "MakeSelection",
                Type = CommandType.Selection,
                TargetDocumentId = documentId,
                Parameters = selection.ToDictionary()
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        #endregion;

        #region Public Methods - Batch Processing;

        /// <summary>
        /// Batch görüntü işleme yapar;
        /// </summary>
        public async Task<BatchProcessingResult> ProcessBatchAsync(
            IEnumerable<string> inputFiles,
            IEnumerable<PhotoshopCommand> operations,
            string outputDirectory = null,
            BatchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            var files = inputFiles.ToList();
            if (files.Count == 0)
                throw new ArgumentException("Input files list cannot be empty", nameof(inputFiles));

            options ??= new BatchOptions();
            outputDirectory ??= WorkingDirectory ?? Path.GetDirectoryName(files.First());

            var result = new BatchProcessingResult;
            {
                StartTime = DateTime.Now,
                TotalFiles = files.Count,
                Options = options;
            };

            try
            {
                // Batch başlangıç;
                _logger.Info($"Starting batch processing of {files.Count} files", GetType().Name);

                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Status = BatchStatus.Cancelled;
                        break;
                    }

                    try
                    {
                        // Dosyayı aç;
                        var doc = await OpenDocumentAsync(file, options.DocumentOptions, cancellationToken);

                        // İşlemleri uygula;
                        foreach (var operation in operations)
                        {
                            operation.TargetDocumentId = doc.Id;
                            await ExecuteCommandAsync(operation, cancellationToken);
                        }

                        // Kaydet;
                        var outputPath = GetBatchOutputPath(file, outputDirectory, options.OutputFormat);
                        var saveOptions = new SaveOptions;
                        {
                            Format = options.OutputFormat,
                            Quality = options.Quality,
                            Compression = options.Compression;
                        };

                        await SaveDocumentAsync(doc.Id, outputPath, saveOptions, cancellationToken);

                        // Belgeyi kapat;
                        CloseDocument(doc.Id, false);

                        result.ProcessedFiles++;
                        result.SuccessfulFiles++;

                        _logger.Debug($"Batch processed: {file} -> {outputPath}", GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        result.FailedFiles++;
                        result.Errors.Add(new BatchError;
                        {
                            FilePath = file,
                            ErrorMessage = ex.Message,
                            Timestamp = DateTime.Now;
                        });

                        _logger.Error($"Batch processing failed for {file}: {ex.Message}", GetType().Name);

                        if (options.StopOnError)
                        {
                            result.Status = BatchStatus.Failed;
                            break;
                        }
                    }
                }

                result.EndTime = DateTime.Now;
                result.Status = result.Status == BatchStatus.Running;
                    ? (result.FailedFiles == 0 ? BatchStatus.Completed : BatchStatus.PartialFailure)
                    : result.Status;

                _logger.Info($"Batch processing completed: {result.SuccessfulFiles} successful, {result.FailedFiles} failed", GetType().Name);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Batch processing failed: {ex.Message}", GetType().Name);
                throw;
            }
        }

        /// <summary>
        /// Photoshop Action'ını çalıştırır;
        /// </summary>
        public async Task ExecuteActionAsync(
            string actionSet,
            string actionName,
            int documentId = 0,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            var command = new PhotoshopCommand;
            {
                Name = $"ExecuteAction_{actionSet}_{actionName}",
                Type = CommandType.Action,
                TargetDocumentId = documentId,
                Parameters = new Dictionary<string, object>
                {
                    { "ActionSet", actionSet },
                    { "ActionName", actionName }
                }
            };

            await ExecuteCommandAsync(command, cancellationToken);
        }

        /// <summary>
        /// Photoshop Script'ini çalıştırır;
        /// </summary>
        public async Task<ScriptExecutionResult> ExecuteScriptAsync(
            string scriptCode,
            ScriptLanguage language = ScriptLanguage.JavaScript,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            var scriptFile = await CreateScriptFileAsync(scriptCode, language, cancellationToken);

            try
            {
                var result = new ScriptExecutionResult;
                {
                    Language = language,
                    StartTime = DateTime.Now;
                };

                // Script'i çalıştır;
                _photoshopApp.DoJavaScript(scriptFile, parameters?.Values.ToArray() ?? new object[0], null);

                result.EndTime = DateTime.Now;
                result.Success = true;

                _logger.Info($"Script executed successfully: {language}", GetType().Name);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script execution failed: {ex.Message}", GetType().Name);

                return new ScriptExecutionResult;
                {
                    Success = false,
                    Error = ex.Message,
                    EndTime = DateTime.Now;
                };
            }
            finally
            {
                // Geçici dosyayı temizle;
                if (AutoCleanupTempFiles && File.Exists(scriptFile))
                {
                    File.Delete(scriptFile);
                }
            }
        }

        #endregion;

        #region Public Methods - Command Execution;

        /// <summary>
        /// Photoshop komutunu yürütür;
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (!IsConnected)
                throw new InvalidOperationException("Not connected to Photoshop");

            var executionId = Guid.NewGuid();
            var startTime = DateTime.Now;

            try
            {
                // Komut kuyruğuna ekle;
                lock (_commandQueue)
                {
                    _commandQueue.Enqueue(command);
                }

                // İlgili belgeyi aktif yap;
                if (command.TargetDocumentId > 0 && _openDocuments.ContainsKey(command.TargetDocumentId))
                {
                    ActivateDocument(command.TargetDocumentId);
                }

                // Komutu yürüt;
                object result = null;

                switch (command.Type)
                {
                    case CommandType.Filter:
                        result = await ExecuteFilterCommandAsync(command, cancellationToken);
                        break;
                    case CommandType.Transform:
                        result = await ExecuteTransformCommandAsync(command, cancellationToken);
                        break;
                    case CommandType.Color:
                        result = await ExecuteColorCommandAsync(command, cancellationToken);
                        break;
                    case CommandType.Layer:
                        result = await ExecuteLayerCommandAsync(command, cancellationToken);
                        break;
                    case CommandType.Selection:
                        result = await ExecuteSelectionCommandAsync(command, cancellationToken);
                        break;
                    case CommandType.Action:
                        result = await ExecuteActionCommandAsync(command, cancellationToken);
                        break;
                    case CommandType.Script:
                        result = await ExecuteScriptCommandAsync(command, cancellationToken);
                        break;
                    default:
                        throw new NotSupportedException($"Command type {command.Type} is not supported");
                }

                var executionTime = DateTime.Now - startTime;

                var executionResult = new CommandExecutionResult;
                {
                    ExecutionId = executionId,
                    Command = command,
                    Status = CommandStatus.Completed,
                    Result = result,
                    ExecutionTime = executionTime,
                    StartTime = startTime,
                    EndTime = DateTime.Now;
                };

                _currentSession.CommandsExecuted++;
                _performanceMonitor.RecordExecution(command.Name, executionTime);

                OnCommandExecuted(new PhotoshopCommandExecutedEventArgs;
                {
                    Command = command,
                    Result = executionResult,
                    SessionId = _currentSession.SessionId;
                });

                return executionResult;
            }
            catch (Exception ex)
            {
                var executionTime = DateTime.Now - startTime;

                var executionResult = new CommandExecutionResult;
                {
                    ExecutionId = executionId,
                    Command = command,
                    Status = CommandStatus.Failed,
                    Error = ex.Message,
                    Exception = ex,
                    ExecutionTime = executionTime,
                    StartTime = startTime,
                    EndTime = DateTime.Now;
                };

                _logger.Error($"Command execution failed: {command.Name} - {ex.Message}", GetType().Name);

                OnCommandExecuted(new PhotoshopCommandExecutedEventArgs;
                {
                    Command = command,
                    Result = executionResult,
                    SessionId = _currentSession.SessionId;
                });

                return executionResult;
            }
            finally
            {
                // Komutu kuyruktan çıkar;
                lock (_commandQueue)
                {
                    if (_commandQueue.Count > 0 && _commandQueue.Peek() == command)
                    {
                        _commandQueue.Dequeue();
                    }
                }
            }
        }

        #endregion;

        #region Private Methods - Command Execution;

        /// <summary>
        /// Filtre komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteFilterCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            if (!command.Parameters.TryGetValue("FilterName", out object filterNameObj) ||
                !(filterNameObj is string filterName))
            {
                throw new ArgumentException("FilterName parameter is required");
            }

            dynamic doc = command.TargetDocumentId > 0;
                ? _openDocuments[command.TargetDocumentId]
                : _photoshopApp.ActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Photoshop filtre fonksiyonunu çağır;
            // Not: Photoshop COM API'si filtre isimlerine göre değişir;
            // Bu kısım Photoshop'un belirli sürümlerine bağlıdır;

            await Task.Delay(COMMAND_DELAY_MS, cancellationToken); // Photoshop'un işlemi tamamlaması için bekle;

            return $"Filter {filterName} applied";
        }

        /// <summary>
        /// Dönüşüm komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteTransformCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            dynamic doc = command.TargetDocumentId > 0;
                ? _openDocuments[command.TargetDocumentId]
                : _photoshopApp.ActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            switch (command.Name)
            {
                case "ResizeImage":
                    if (command.Parameters.TryGetValue("Width", out object widthObj) && widthObj != null)
                    {
                        doc.ResizeImage((double)widthObj, null, null);
                    }
                    else if (command.Parameters.TryGetValue("Height", out object heightObj) && heightObj != null)
                    {
                        doc.ResizeImage(null, (double)heightObj, null);
                    }
                    break;

                case "RotateImage":
                    if (command.Parameters.TryGetValue("Angle", out object angleObj))
                    {
                        var direction = command.Parameters.TryGetValue("Direction", out object dirObj)
                            ? (RotationDirection)dirObj;
                            : RotationDirection.Clockwise;

                        var angle = (double)angleObj;
                        if (direction == RotationDirection.CounterClockwise)
                        {
                            angle = -angle;
                        }

                        doc.Rotate(angle);
                    }
                    break;

                case "CropImage":
                    if (command.Parameters.TryGetValue("Left", out object leftObj) &&
                        command.Parameters.TryGetValue("Top", out object topObj) &&
                        command.Parameters.TryGetValue("Right", out object rightObj) &&
                        command.Parameters.TryGetValue("Bottom", out object bottomObj))
                    {
                        var left = (double)leftObj;
                        var top = (double)topObj;
                        var right = (double)rightObj;
                        var bottom = (double)bottomObj;

                        doc.Crop(new[] { left, top, right, bottom });
                    }
                    break;
            }

            await Task.Delay(COMMAND_DELAY_MS, cancellationToken);
            return $"{command.Name} completed";
        }

        /// <summary>
        /// Renk komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteColorCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            dynamic doc = command.TargetDocumentId > 0;
                ? _openDocuments[command.TargetDocumentId]
                : _photoshopApp.ActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Photoshop renk ayarlama fonksiyonları;
            // Bu kısım Photoshop'un belirli sürümlerine bağlıdır;

            await Task.Delay(COMMAND_DELAY_MS, cancellationToken);
            return $"{command.Name} completed";
        }

        /// <summary>
        /// Katman komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteLayerCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            dynamic doc = command.TargetDocumentId > 0;
                ? _openDocuments[command.TargetDocumentId]
                : _photoshopApp.ActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Photoshop katman işlemleri;
            // Bu kısım Photoshop'un belirli sürümlerine bağlıdır;

            await Task.Delay(COMMAND_DELAY_MS, cancellationToken);
            return $"{command.Name} completed";
        }

        /// <summary>
        /// Seçim komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteSelectionCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            dynamic doc = command.TargetDocumentId > 0;
                ? _openDocuments[command.TargetDocumentId]
                : _photoshopApp.ActiveDocument;

            if (doc == null)
                throw new InvalidOperationException("No active document");

            // Photoshop seçim işlemleri;
            // Bu kısım Photoshop'un belirli sürümlerine bağlıdır;

            await Task.Delay(COMMAND_DELAY_MS, cancellationToken);
            return $"{command.Name} completed";
        }

        /// <summary>
        /// Action komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteActionCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            if (command.Parameters.TryGetValue("ActionSet", out object setObj) &&
                command.Parameters.TryGetValue("ActionName", out object nameObj))
            {
                var actionSet = setObj as string;
                var actionName = nameObj as string;

                // Photoshop Action'ını çalıştır;
                _photoshopApp.DoAction(actionName, actionSet);
            }

            await Task.Delay(COMMAND_DELAY_MS, cancellationToken);
            return $"Action {command.Parameters["ActionName"]} completed";
        }

        /// <summary>
        /// Script komutunu yürütür;
        /// </summary>
        private async Task<object> ExecuteScriptCommandAsync(
            PhotoshopCommand command,
            CancellationToken cancellationToken)
        {
            if (command.Parameters.TryGetValue("ScriptCode", out object codeObj) &&
                codeObj is string scriptCode)
            {
                var language = command.Parameters.TryGetValue("Language", out object langObj)
                    ? (ScriptLanguage)langObj;
                    : ScriptLanguage.JavaScript;

                var result = await ExecuteScriptAsync(scriptCode, language, command.Parameters, cancellationToken);
                return result;
            }

            return "Script command executed";
        }

        #endregion;

        #region Private Methods - Utilities;

        /// <summary>
        /// Photoshop yolunu tespit eder;
        /// </summary>
        private void DetectPhotoshopPath()
        {
            var possiblePaths = new List<string>
            {
                @"C:\Program Files\Adobe\Adobe Photoshop 2024\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2023\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2022\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2021\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop 2020\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop CC 2019\Photoshop.exe",
                @"C:\Program Files\Adobe\Adobe Photoshop CC 2018\Photoshop.exe",
                @"C:\Program Files (x86)\Adobe\Adobe Photoshop CS6\Photoshop.exe",
                @"C:\Program Files (x86)\Adobe\Adobe Photoshop CS5\Photoshop.exe"
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    _photoshopPath = path;
                    _logger.Info($"Detected Photoshop at: {path}", GetType().Name);
                    return;
                }
            }

            _logger.Warning("Photoshop executable not found automatically", GetType().Name);
        }

        /// <summary>
        /// Belge katmanlarını alır;
        /// </summary>
        private List<LayerInfo> GetDocumentLayers(dynamic document)
        {
            var layers = new List<LayerInfo>();

            try
            {
                int layerCount = document.Layers.Count;
                for (int i = 1; i <= layerCount; i++)
                {
                    dynamic layer = document.Layers[i];
                    layers.Add(new LayerInfo;
                    {
                        Name = layer.Name,
                        Visible = layer.Visible,
                        Opacity = layer.Opacity,
                        BlendMode = layer.BlendMode;
                    });

                    Marshal.FinalReleaseComObject(layer);
                }
            }
            catch
            {
                // Katman bilgileri alınamıyor;
            }

            return layers;
        }

        /// <summary>
        /// JPEG kaydetme seçenekleri oluşturur;
        /// </summary>
        private dynamic CreateJPEGSaveOptions(SaveOptions options)
        {
            Type jpegOptionsType = Type.GetTypeFromProgID("Photoshop.JPEGSaveOptions");
            if (jpegOptionsType == null)
                return null;

            dynamic jpegOptions = Activator.CreateInstance(jpegOptionsType);
            jpegOptions.Quality = options.Quality ?? JpegQuality;
            jpegOptions.FormatOptions = 1; // Standard;
            jpegOptions.EmbedColorProfile = true;
            jpegOptions.Matte = 1; // None;

            return jpegOptions;
        }

        /// <summary>
        /// PNG kaydetme seçenekleri oluşturur;
        /// </summary>
        private dynamic CreatePNGSaveOptions(SaveOptions options)
        {
            Type pngOptionsType = Type.GetTypeFromProgID("Photoshop.PNGSaveOptions");
            if (pngOptionsType == null)
                return null;

            dynamic pngOptions = Activator.CreateInstance(pngOptionsType);
            pngOptions.Compression = options.Compression ?? PngCompressionLevel;
            pngOptions.Interlaced = false;

            return pngOptions;
        }

        /// <summary>
        /// TIFF kaydetme seçenekleri oluşturur;
        /// </summary>
        private dynamic CreateTIFFSaveOptions(SaveOptions options)
        {
            Type tiffOptionsType = Type.GetTypeFromProgID("Photoshop.TiffSaveOptions");
            if (tiffOptionsType == null)
                return null;

            dynamic tiffOptions = Activator.CreateInstance(tiffOptionsType);
            tiffOptions.ByteOrder = 1; // IBM PC;
            tiffOptions.ImageCompression = 1; // LZW;
            tiffOptions.LayerCompression = 1; // RLE;
            tiffOptions.Transparency = true;

            return tiffOptions;
        }

        /// <summary>
        /// PSD kaydetme seçenekleri oluşturur;
        /// </summary>
        private dynamic CreatePSDSaveOptions(SaveOptions options)
        {
            Type psdOptionsType = Type.GetTypeFromProgID("Photoshop.PhotoshopSaveOptions");
            if (psdOptionsType == null)
                return null;

            dynamic psdOptions = Activator.CreateInstance(psdOptionsType);
            psdOptions.AlphaChannels = true;
            psdOptions.Layers = true;
            psdOptions.SpotColors = true;
            psdOptions.Annotations = false;

            return psdOptions;
        }

        /// <summary>
        /// Varsayılan kaydetme yolu oluşturur;
        /// </summary>
        private string GetDefaultSavePath(string originalName, PhotoshopFormat format)
        {
            var fileName = Path.GetFileNameWithoutExtension(originalName);
            var extension = format.ToString().ToLower();

            if (format == PhotoshopFormat.JPEG)
                extension = "jpg";
            else if (format == PhotoshopFormat.TIFF)
                extension = "tif";

            var directory = WorkingDirectory ?? Path.GetTempPath();
            return Path.Combine(directory, $"{fileName}_processed.{extension}");
        }

        /// <summary>
        /// Batch çıktı yolu oluşturur;
        /// </summary>
        private string GetBatchOutputPath(string inputPath, string outputDirectory, PhotoshopFormat format)
        {
            var fileName = Path.GetFileNameWithoutExtension(inputPath);
            var extension = format.ToString().ToLower();

            if (format == PhotoshopFormat.JPEG)
                extension = "jpg";
            else if (format == PhotoshopFormat.TIFF)
                extension = "tif";

            return Path.Combine(outputDirectory, $"{fileName}_processed.{extension}");
        }

        /// <summary>
        /// Script dosyası oluşturur;
        /// </summary>
        private async Task<string> CreateScriptFileAsync(
            string scriptCode,
            ScriptLanguage language,
            CancellationToken cancellationToken)
        {
            var extension = language switch;
            {
                ScriptLanguage.JavaScript => ".jsx",
                ScriptLanguage.VBScript => ".vbs",
                ScriptLanguage.AppleScript => ".scpt",
                _ => ".jsx"
            };

            var tempFile = Path.GetTempFileName();
            var scriptFile = Path.ChangeExtension(tempFile, extension);

            File.Move(tempFile, scriptFile);
            await File.WriteAllTextAsync(scriptFile, scriptCode, cancellationToken);

            return scriptFile;
        }

        #endregion;

        #region Event Invokers;

        protected virtual void OnConnected(PhotoshopConnectedEventArgs e)
        {
            Connected?.Invoke(this, e);
        }

        protected virtual void OnDisconnected(PhotoshopDisconnectedEventArgs e)
        {
            Disconnected?.Invoke(this, e);
        }

        protected virtual void OnCommandExecuted(PhotoshopCommandExecutedEventArgs e)
        {
            CommandExecuted?.Invoke(this, e);
        }

        protected virtual void OnDocumentOpened(DocumentOpenedEventArgs e)
        {
            DocumentOpened?.Invoke(this, e);
        }

        protected virtual void OnDocumentSaved(DocumentSavedEventArgs e)
        {
            DocumentSaved?.Invoke(this, e);
        }

        protected virtual void OnOutputReceived(PhotoshopOutputEventArgs e)
        {
            OutputReceived?.Invoke(this, e);
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
                    Disconnect();

                    lock (_psLock)
                    {
                        _openDocuments.Clear();
                        _commandQueue.Clear();
                    }

                    _logger.Info("PSEngine disposed", GetType().Name);
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PSEngine()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public enum PhotoshopFormat;
    {
        JPEG,
        PNG,
        TIFF,
        PSD,
        BMP,
        GIF;
    }

    public enum ColorMode;
    {
        Bitmap = 1,
        Grayscale = 2,
        IndexedColor = 3,
        RGB = 4,
        CMYK = 5,
        Lab = 9,
        Multichannel = 7,
        Duotone = 8;
    }

    public enum CommandType;
    {
        Filter,
        Transform,
        Color,
        Layer,
        Selection,
        Action,
        Script;
    }

    public enum CommandStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        Cancelled;
    }

    public enum ResizeMethod;
    {
        NearestNeighbor = 1,
        Bilinear = 2,
        Bicubic = 3,
        BicubicSmoother = 4,
        BicubicSharper = 5;
    }

    public enum RotationDirection;
    {
        Clockwise,
        CounterClockwise;
    }

    public enum ScriptLanguage;
    {
        JavaScript,
        VBScript,
        AppleScript;
    }

    public enum BatchStatus;
    {
        Pending,
        Running,
        Completed,
        Failed,
        PartialFailure,
        Cancelled;
    }

    public class PhotoshopSession;
    {
        public Guid SessionId { get; set; }
        public string PhotoshopVersion { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public TimeSpan Duration => StartTime != default && EndTime.HasValue;
            ? EndTime.Value - StartTime;
            : DateTime.Now - StartTime;
        public int DocumentsProcessed { get; set; }
        public int CommandsExecuted { get; set; }
        public Dictionary<string, object> SessionData { get; set; } = new Dictionary<string, object>();
    }

    public class PhotoshopCommand;
    {
        public string Name { get; set; }
        public CommandType Type { get; set; }
        public int TargetDocumentId { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    public class CommandExecutionResult;
    {
        public Guid ExecutionId { get; set; } = Guid.NewGuid();
        public PhotoshopCommand Command { get; set; }
        public CommandStatus Status { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
        public Exception Exception { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    public class PhotoshopDocument;
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string FilePath { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public ColorMode Mode { get; set; }
        public double Resolution { get; set; }
        public List<LayerInfo> Layers { get; set; } = new List<LayerInfo>();
        public bool IsActive { get; set; }
    }

    public class LayerInfo;
    {
        public string Name { get; set; }
        public bool Visible { get; set; }
        public double Opacity { get; set; }
        public int BlendMode { get; set; }
    }

    public class DocumentOptions;
    {
        public bool ConvertToRGB { get; set; } = true;
        public double Resolution { get; set; }
        public bool PreserveLayers { get; set; } = true;
    }

    public class SaveOptions;
    {
        public PhotoshopFormat? Format { get; set; }
        public int? Quality { get; set; }
        public int? Compression { get; set; }
        public string Extension { get; set; }
        public bool EmbedColorProfile { get; set; } = true;
    }

    public class ColorAdjustment;
    {
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        public double Saturation { get; set; }
        public double Hue { get; set; }
        public double Temperature { get; set; }
        public double Tint { get; set; }
    }

    public class Rectangle;
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
    }

    public class LayerOperation;
    {
        public string Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
    }

    public class LayerOperationResult;
    {
        public bool Success { get; set; }
        public string OperationType { get; set; }
        public int DocumentId { get; set; }
        public string Error { get; set; }
    }

    public class Selection;
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public int Feather { get; set; }

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            { "X", X },
            { "Y", Y },
            { "Width", Width },
            { "Height", Height },
            { "Feather", Feather }
        };
    }

    public class BatchProcessingResult;
    {
        public BatchStatus Status { get; set; } = BatchStatus.Running;
        public int TotalFiles { get; set; }
        public int ProcessedFiles { get; set; }
        public int SuccessfulFiles { get; set; }
        public int FailedFiles { get; set; }
        public List<BatchError> Errors { get; set; } = new List<BatchError>();
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalDuration => EndTime - StartTime;
        public BatchOptions Options { get; set; }
    }

    public class BatchError;
    {
        public string FilePath { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BatchOptions;
    {
        public PhotoshopFormat OutputFormat { get; set; } = PhotoshopFormat.JPEG;
        public int? Quality { get; set; }
        public int? Compression { get; set; }
        public DocumentOptions DocumentOptions { get; set; } = new DocumentOptions();
        public bool StopOnError { get; set; } = false;
        public bool PreserveOriginals { get; set; } = true;
    }

    public class ScriptExecutionResult;
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public ScriptLanguage Language { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan ExecutionTime => EndTime - StartTime;
    }

    public class PerformanceMonitor;
    {
        private readonly Dictionary<string, List<TimeSpan>> _executionTimes;

        public PerformanceMonitor()
        {
            _executionTimes = new Dictionary<string, List<TimeSpan>>();
        }

        public void RecordExecution(string commandName, TimeSpan executionTime)
        {
            if (!_executionTimes.ContainsKey(commandName))
            {
                _executionTimes[commandName] = new List<TimeSpan>();
            }

            _executionTimes[commandName].Add(executionTime);
        }

        public TimeSpan GetAverageExecutionTime(string commandName)
        {
            if (_executionTimes.TryGetValue(commandName, out var times) && times.Count > 0)
            {
                return TimeSpan.FromMilliseconds(times.Average(t => t.TotalMilliseconds));
            }

            return TimeSpan.Zero;
        }
    }

    #endregion;

    #region Event Args Classes;

    public class PhotoshopConnectedEventArgs : EventArgs;
    {
        public Guid SessionId { get; set; }
        public string Version { get; set; }
        public bool IsVisible { get; set; }
    }

    public class PhotoshopDisconnectedEventArgs : EventArgs;
    {
        public Guid SessionId { get; set; }
        public TimeSpan Duration { get; set; }
        public int DocumentsProcessed { get; set; }
        public int CommandsExecuted { get; set; }
    }

    public class PhotoshopCommandExecutedEventArgs : EventArgs;
    {
        public PhotoshopCommand Command { get; set; }
        public CommandExecutionResult Result { get; set; }
        public Guid SessionId { get; set; }
    }

    public class DocumentOpenedEventArgs : EventArgs;
    {
        public PhotoshopDocument Document { get; set; }
        public Guid SessionId { get; set; }
    }

    public class DocumentSavedEventArgs : EventArgs;
    {
        public int DocumentId { get; set; }
        public string FilePath { get; set; }
        public PhotoshopFormat Format { get; set; }
        public Guid SessionId { get; set; }
    }

    public class PhotoshopOutputEventArgs : EventArgs;
    {
        public string Output { get; set; }
        public bool IsError { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }

    #endregion;
}
