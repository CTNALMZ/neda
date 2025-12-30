using NEDA.ContentCreation.AssetPipeline.FormatConverters;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.EngineIntegration.RenderManager;
using NEDA.EngineIntegration.Unreal;
using NEDA.KnowledgeBase.DataManagement;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.Services.FileService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using static NEDA.Core.Common.Constants;

namespace NEDA.EngineIntegration.AssetManager;
{
    /// <summary>
    /// Asset import işlemlerini yöneten ana sınıf;
    /// Çoklu format, batch processing ve validation destekler;
    /// </summary>
    public class AssetImporter : IAssetImporter, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IFileManager _fileManager;
        private readonly IRenderEngine _renderEngine;
        private readonly IDatabase _database;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IAssetValidator _assetValidator;
        private readonly IImportProgressReporter _progressReporter;

        private readonly Dictionary<string, IFormatHandler> _formatHandlers;
        private readonly HashSet<string> _supportedExtensions;
        private readonly ImportConfiguration _config;

        private bool _isDisposed;
        private readonly object _importLock = new object();

        /// <summary>
        /// AssetImporter constructor;
        /// </summary>
        public AssetImporter(
            ILogger logger,
            IFileManager fileManager,
            IRenderEngine renderEngine,
            IDatabase database,
            ICryptoEngine cryptoEngine,
            IAssetValidator assetValidator = null,
            IImportProgressReporter progressReporter = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileManager = fileManager ?? throw new ArgumentNullException(nameof(fileManager));
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _assetValidator = assetValidator;
            _progressReporter = progressReporter;

            _formatHandlers = new Dictionary<string, IFormatHandler>(StringComparer.OrdinalIgnoreCase);
            _supportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _config = LoadConfiguration();

            InitializeFormatHandlers();
            RegisterDefaultFormats();

            _logger.Info("AssetImporter initialized successfully");
        }

        /// <summary>
        /// Asset import etmek için ana metod;
        /// </summary>
        /// <param name="sourcePath">Import edilecek dosya/dizin yolu</param>
        /// <param name="destinationPath">Hedef yolu (opsiyonel)</param>
        /// <param name="options">Import seçenekleri</param>
        /// <returns>Import edilen asset'lerin metadata listesi</returns>
        public async Task<IEnumerable<AssetMetadata>> ImportAsync(
            string sourcePath,
            string destinationPath = null,
            ImportOptions options = null)
        {
            ValidateImportParameters(sourcePath);

            options ??= ImportOptions.Default;
            destinationPath ??= GetDefaultDestinationPath();

            _logger.Info($"Starting import process: {sourcePath} -> {destinationPath}");

            try
            {
                var importResults = new List<AssetMetadata>();

                if (_fileManager.IsDirectory(sourcePath))
                {
                    importResults = await ImportDirectoryAsync(sourcePath, destinationPath, options);
                }
                else;
                {
                    var result = await ImportSingleFileAsync(sourcePath, destinationPath, options);
                    if (result != null)
                        importResults.Add(result);
                }

                await PostImportProcessingAsync(importResults, options);

                _logger.Info($"Import completed successfully. {importResults.Count} assets imported.");
                return importResults;
            }
            catch (Exception ex)
            {
                _logger.Error($"Import failed: {ex.Message}", ex);
                throw new AssetImportException($"Failed to import assets from {sourcePath}", ex);
            }
        }

        /// <summary>
        /// Batch import işlemi;
        /// </summary>
        public async Task<BatchImportResult> ImportBatchAsync(
            IEnumerable<string> sourcePaths,
            string destinationPath,
            BatchImportOptions options)
        {
            Guard.ArgumentNotNull(sourcePaths, nameof(sourcePaths));
            Guard.ArgumentNotNullOrEmpty(destinationPath, nameof(destinationPath));
            Guard.ArgumentNotNull(options, nameof(options));

            _progressReporter?.ReportBatchStart(sourcePaths.Count());

            var result = new BatchImportResult;
            {
                StartTime = DateTime.UtcNow,
                TotalFiles = sourcePaths.Count()
            };

            var successfulImports = new List<AssetMetadata>();
            var failedImports = new List<FailedImport>();

            foreach (var sourcePath in sourcePaths)
            {
                try
                {
                    _progressReporter?.ReportFileStart(sourcePath);

                    var metadata = await ImportSingleFileAsync(
                        sourcePath,
                        destinationPath,
                        options.ImportOptions);

                    successfulImports.Add(metadata);
                    result.SuccessfulCount++;

                    _progressReporter?.ReportFileComplete(sourcePath, true);
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to import {sourcePath}: {ex.Message}");

                    failedImports.Add(new FailedImport;
                    {
                        SourcePath = sourcePath,
                        ErrorMessage = ex.Message,
                        Exception = ex;
                    });

                    result.FailedCount++;
                    _progressReporter?.ReportFileComplete(sourcePath, false);

                    if (options.StopOnFirstError)
                        throw;
                }

                if (options.MaxConcurrentImports > 1)
                {
                    await Task.Delay(options.DelayBetweenImports);
                }
            }

            result.SuccessfulImports = successfulImports;
            result.FailedImports = failedImports;
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;

            _progressReporter?.ReportBatchComplete(result);

            return result;
        }

        /// <summary>
        /// Format handler kaydetme;
        /// </summary>
        public void RegisterFormatHandler(string extension, IFormatHandler handler)
        {
            Guard.ArgumentNotNullOrEmpty(extension, nameof(extension));
            Guard.ArgumentNotNull(handler, nameof(handler));

            lock (_importLock)
            {
                if (extension.StartsWith("."))
                    extension = extension.Substring(1);

                _formatHandlers[extension] = handler;
                _supportedExtensions.Add(extension);

                _logger.Debug($"Format handler registered for .{extension}");
            }
        }

        /// <summary>
        /// Desteklenen formatları getirir;
        /// </summary>
        public IEnumerable<string> GetSupportedFormats()
        {
            return _supportedExtensions;
                .Select(ext => $".{ext}")
                .OrderBy(ext => ext)
                .ToList();
        }

        /// <summary>
        /// Asset validation için;
        /// </summary>
        public async Task<ValidationResult> ValidateAssetAsync(string assetPath, ValidationOptions options = null)
        {
            Guard.ArgumentNotNullOrEmpty(assetPath, nameof(assetPath));

            if (!File.Exists(assetPath))
                throw new FileNotFoundException($"Asset not found: {assetPath}");

            var extension = Path.GetExtension(assetPath).TrimStart('.');

            if (!_formatHandlers.ContainsKey(extension))
                throw new NotSupportedException($"Format not supported: .{extension}");

            var handler = _formatHandlers[extension];
            return await handler.ValidateAsync(assetPath, options);
        }

        /// <summary>
        /// Import konfigürasyonunu güncelle;
        /// </summary>
        public void UpdateConfiguration(Action<ImportConfiguration> configAction)
        {
            Guard.ArgumentNotNull(configAction, nameof(configAction));

            lock (_importLock)
            {
                configAction.Invoke(_config);
                SaveConfiguration(_config);

                _logger.Info("Import configuration updated");
            }
        }

        /// <summary>
        /// Kaynakları temizle;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    foreach (var handler in _formatHandlers.Values.OfType<IDisposable>())
                    {
                        handler.Dispose();
                    }

                    _formatHandlers.Clear();
                    _supportedExtensions.Clear();
                }

                _isDisposed = true;
            }
        }

        #region Private Methods;

        private async Task<AssetMetadata> ImportSingleFileAsync(
            string sourcePath,
            string destinationPath,
            ImportOptions options)
        {
            var extension = Path.GetExtension(sourcePath).TrimStart('.');

            if (!_formatHandlers.TryGetValue(extension, out var handler))
            {
                throw new NotSupportedException(
                    $"Unsupported file format: .{extension}. " +
                    $"Supported formats: {string.Join(", ", GetSupportedFormats())}");
            }

            // Pre-import validation;
            if (options.EnableValidation && _assetValidator != null)
            {
                var validationResult = await _assetValidator.ValidateAsync(sourcePath);
                if (!validationResult.IsValid)
                {
                    throw new AssetValidationException(
                        $"Asset validation failed: {validationResult.ErrorMessage}");
                }
            }

            // Create destination directory if not exists;
            var destinationDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(destinationDir) && !Directory.Exists(destinationDir))
            {
                Directory.CreateDirectory(destinationDir);
            }

            // Process the asset;
            var processedAsset = await handler.ProcessAsync(sourcePath, destinationPath, options);

            // Generate metadata;
            var metadata = await GenerateMetadataAsync(processedAsset, sourcePath);

            // Store metadata in database;
            await StoreMetadataAsync(metadata);

            // Post-processing;
            if (options.GenerateThumbnails)
            {
                await GenerateThumbnailAsync(metadata);
            }

            if (options.EncryptAssets)
            {
                await EncryptAssetAsync(metadata);
            }

            _logger.Debug($"File imported successfully: {sourcePath}");
            return metadata;
        }

        private async Task<List<AssetMetadata>> ImportDirectoryAsync(
            string sourceDir,
            string destinationDir,
            ImportOptions options)
        {
            var results = new List<AssetMetadata>();

            // Get all supported files in directory;
            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories)
                .Where(f => IsSupportedFormat(Path.GetExtension(f)))
                .ToList();

            _logger.Info($"Found {files.Count} supported files in directory: {sourceDir}");

            // Process files based on concurrency settings;
            if (options.MaxConcurrency > 1)
            {
                var tasks = new List<Task<AssetMetadata>>();
                var semaphore = new System.Threading.SemaphoreSlim(options.MaxConcurrency);

                foreach (var file in files)
                {
                    await semaphore.WaitAsync();

                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var relativePath = Path.GetRelativePath(sourceDir, file);
                            var destPath = Path.Combine(destinationDir, relativePath);

                            return await ImportSingleFileAsync(file, destPath, options);
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }));
                }

                var importResults = await Task.WhenAll(tasks);
                results.AddRange(importResults.Where(r => r != null));
            }
            else;
            {
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(sourceDir, file);
                    var destPath = Path.Combine(destinationDir, relativePath);

                    var result = await ImportSingleFileAsync(file, destPath, options);
                    if (result != null)
                        results.Add(result);
                }
            }

            return results;
        }

        private async Task<AssetMetadata> GenerateMetadataAsync(
            ProcessedAsset asset,
            string originalPath)
        {
            var fileInfo = new FileInfo(asset.ProcessedPath);

            var metadata = new AssetMetadata;
            {
                Id = Guid.NewGuid(),
                OriginalFileName = Path.GetFileName(originalPath),
                FilePath = asset.ProcessedPath,
                FileSize = fileInfo.Length,
                FileExtension = Path.GetExtension(originalPath).TrimStart('.'),
                ImportDate = DateTime.UtcNow,
                FormatVersion = asset.FormatVersion,
                CompressionRatio = asset.CompressionRatio,
                IsEncrypted = false,
                Checksum = await CalculateChecksumAsync(asset.ProcessedPath),
                Tags = new List<string>(),
                CustomProperties = new Dictionary<string, object>()
            };

            // Extract additional metadata based on format;
            if (_formatHandlers.TryGetValue(metadata.FileExtension, out var handler))
            {
                var additionalMetadata = await handler.ExtractMetadataAsync(asset.ProcessedPath);
                metadata.CustomProperties = additionalMetadata;

                // Extract tags from metadata;
                metadata.Tags = ExtractTagsFromMetadata(additionalMetadata);
            }

            return metadata;
        }

        private async Task StoreMetadataAsync(AssetMetadata metadata)
        {
            try
            {
                await _database.ExecuteAsync("sp_InsertAssetMetadata", new;
                {
                    metadata.Id,
                    metadata.OriginalFileName,
                    metadata.FilePath,
                    metadata.FileSize,
                    metadata.FileExtension,
                    metadata.ImportDate,
                    metadata.FormatVersion,
                    metadata.CompressionRatio,
                    metadata.IsEncrypted,
                    metadata.Checksum,
                    Tags = string.Join(";", metadata.Tags),
                    CustomProperties = SerializeCustomProperties(metadata.CustomProperties)
                });

                _logger.Debug($"Metadata stored for asset: {metadata.OriginalFileName}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to store metadata: {ex.Message}");
                // Continue without throwing - metadata storage failure shouldn't fail entire import;
            }
        }

        private async Task GenerateThumbnailAsync(AssetMetadata metadata)
        {
            try
            {
                var thumbnailPath = Path.Combine(
                    Path.GetDirectoryName(metadata.FilePath),
                    "Thumbnails",
                    $"{Path.GetFileNameWithoutExtension(metadata.FilePath)}_thumb.jpg");

                await _renderEngine.GenerateThumbnailAsync(metadata.FilePath, thumbnailPath);

                metadata.ThumbnailPath = thumbnailPath;
                _logger.Debug($"Thumbnail generated: {thumbnailPath}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to generate thumbnail: {ex.Message}");
            }
        }

        private async Task EncryptAssetAsync(AssetMetadata metadata)
        {
            try
            {
                var encryptedPath = metadata.FilePath + ".encrypted";
                await _cryptoEngine.EncryptFileAsync(metadata.FilePath, encryptedPath);

                // Replace original with encrypted version;
                File.Delete(metadata.FilePath);
                File.Move(encryptedPath, metadata.FilePath);

                metadata.IsEncrypted = true;
                metadata.EncryptionKeyId = _cryptoEngine.CurrentKeyId;

                _logger.Debug($"Asset encrypted: {metadata.FilePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to encrypt asset: {ex.Message}", ex);
                throw new AssetEncryptionException($"Failed to encrypt asset: {metadata.FilePath}", ex);
            }
        }

        private async Task PostImportProcessingAsync(
            IEnumerable<AssetMetadata> importedAssets,
            ImportOptions options)
        {
            if (options.UpdateAssetDatabase)
            {
                await UpdateAssetDatabaseAsync(importedAssets);
            }

            if (options.SendNotifications)
            {
                await SendImportNotificationsAsync(importedAssets);
            }

            if (options.CleanupTempFiles)
            {
                CleanupTemporaryFiles();
            }
        }

        private async Task UpdateAssetDatabaseAsync(IEnumerable<AssetMetadata> assets)
        {
            // Update global asset registry
            foreach (var asset in assets)
            {
                await _database.ExecuteAsync("sp_UpdateAssetRegistry", new;
                {
                    asset.Id,
                    asset.FilePath,
                    LastAccessed = DateTime.UtcNow;
                });
            }

            _logger.Info($"Asset database updated with {assets.Count()} entries");
        }

        private async Task SendImportNotificationsAsync(IEnumerable<AssetMetadata> assets)
        {
            // Implementation would integrate with NEDA.Services.NotificationService;
            // This is a placeholder for the actual implementation;
            _logger.Debug($"Import notifications would be sent for {assets.Count()} assets");
            await Task.CompletedTask;
        }

        private void CleanupTemporaryFiles()
        {
            var tempDir = Path.GetTempPath();
            var tempFiles = Directory.GetFiles(tempDir, "neda_import_*.tmp");

            foreach (var tempFile in tempFiles)
            {
                try
                {
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to delete temp file {tempFile}: {ex.Message}");
                }
            }

            _logger.Debug($"Cleaned up {tempFiles.Length} temporary files");
        }

        private void InitializeFormatHandlers()
        {
            // Built-in format handlers will be initialized here;
            // Actual implementations would be loaded via dependency injection;
        }

        private void RegisterDefaultFormats()
        {
            // Register common 3D formats;
            RegisterFormatHandler("fbx", new FbxFormatHandler());
            RegisterFormatHandler("obj", new ObjFormatHandler());
            RegisterFormatHandler("gltf", new GltfFormatHandler());
            RegisterFormatHandler("glb", new GlbFormatHandler());

            // Register image formats;
            RegisterFormatHandler("png", new ImageFormatHandler());
            RegisterFormatHandler("jpg", new ImageFormatHandler());
            RegisterFormatHandler("jpeg", new ImageFormatHandler());
            RegisterFormatHandler("tga", new ImageFormatHandler());
            RegisterFormatHandler("tiff", new ImageFormatHandler());

            // Register audio formats;
            RegisterFormatHandler("wav", new AudioFormatHandler());
            RegisterFormatHandler("mp3", new AudioFormatHandler());
            RegisterFormatHandler("ogg", new AudioFormatHandler());

            _logger.Info($"Registered {_formatHandlers.Count} default format handlers");
        }

        private bool IsSupportedFormat(string extension)
        {
            if (string.IsNullOrEmpty(extension))
                return false;

            var ext = extension.TrimStart('.').ToLowerInvariant();
            return _supportedExtensions.Contains(ext);
        }

        private async Task<string> CalculateChecksumAsync(string filePath)
        {
            using (var stream = File.OpenRead(filePath))
            {
                using (var sha256 = System.Security.Cryptography.SHA256.Create())
                {
                    var hash = await sha256.ComputeHashAsync(stream);
                    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
                }
            }
        }

        private List<string> ExtractTagsFromMetadata(Dictionary<string, object> metadata)
        {
            var tags = new List<string>();

            // Extract tags from specific metadata fields;
            if (metadata.TryGetValue("AssetType", out var assetType))
                tags.Add($"Type:{assetType}");

            if (metadata.TryGetValue("Author", out var author))
                tags.Add($"Author:{author}");

            if (metadata.TryGetValue("Software", out var software))
                tags.Add($"Software:{software}");

            return tags.Distinct().ToList();
        }

        private string SerializeCustomProperties(Dictionary<string, object> properties)
        {
            // Simple JSON serialization for storage;
            return System.Text.Json.JsonSerializer.Serialize(properties);
        }

        private ImportConfiguration LoadConfiguration()
        {
            // Load from configuration file or database;
            // Default configuration;
            return new ImportConfiguration;
            {
                MaxConcurrentImports = 4,
                DefaultDestination = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "NEDA",
                    "ImportedAssets"),
                EnableCompression = true,
                CompressionLevel = CompressionLevel.Balanced,
                GenerateThumbnails = true,
                ThumbnailSize = new System.Drawing.Size(256, 256),
                PreserveDirectoryStructure = true,
                OverwriteExisting = false,
                ValidateOnImport = true,
                BackupOriginal = true,
                TemporaryFileRetentionDays = 7;
            };
        }

        private void SaveConfiguration(ImportConfiguration config)
        {
            // Save to configuration file or database;
            // Implementation depends on configuration system;
        }

        private string GetDefaultDestinationPath()
        {
            return _config.DefaultDestination;
        }

        private void ValidateImportParameters(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source path cannot be null or empty", nameof(sourcePath));

            if (!File.Exists(sourcePath) && !Directory.Exists(sourcePath))
                throw new FileNotFoundException($"Source not found: {sourcePath}");
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    public interface IAssetImporter;
    {
        Task<IEnumerable<AssetMetadata>> ImportAsync(
            string sourcePath,
            string destinationPath = null,
            ImportOptions options = null);

        Task<BatchImportResult> ImportBatchAsync(
            IEnumerable<string> sourcePaths,
            string destinationPath,
            BatchImportOptions options);

        void RegisterFormatHandler(string extension, IFormatHandler handler);
        IEnumerable<string> GetSupportedFormats();
        Task<ValidationResult> ValidateAssetAsync(string assetPath, ValidationOptions options = null);
        void UpdateConfiguration(Action<ImportConfiguration> configAction);
    }

    public interface IFormatHandler : IDisposable
    {
        Task<ProcessedAsset> ProcessAsync(string sourcePath, string destinationPath, ImportOptions options);
        Task<ValidationResult> ValidateAsync(string assetPath, ValidationOptions options = null);
        Task<Dictionary<string, object>> ExtractMetadataAsync(string assetPath);
    }

    public interface IAssetValidator;
    {
        Task<ValidationResult> ValidateAsync(string assetPath);
    }

    public interface IImportProgressReporter;
    {
        void ReportBatchStart(int totalFiles);
        void ReportFileStart(string filePath);
        void ReportFileComplete(string filePath, bool success);
        void ReportBatchComplete(BatchImportResult result);
    }

    public class AssetMetadata;
    {
        public Guid Id { get; set; }
        public string OriginalFileName { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public string FileExtension { get; set; }
        public DateTime ImportDate { get; set; }
        public string FormatVersion { get; set; }
        public double? CompressionRatio { get; set; }
        public bool IsEncrypted { get; set; }
        public string EncryptionKeyId { get; set; }
        public string Checksum { get; set; }
        public string ThumbnailPath { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, object> CustomProperties { get; set; }
    }

    public class ProcessedAsset;
    {
        public string OriginalPath { get; set; }
        public string ProcessedPath { get; set; }
        public string FormatVersion { get; set; }
        public double CompressionRatio { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    public class ImportOptions;
    {
        public static ImportOptions Default => new ImportOptions();

        public bool EnableCompression { get; set; } = true;
        public CompressionLevel CompressionLevel { get; set; } = CompressionLevel.Balanced;
        public bool GenerateThumbnails { get; set; } = true;
        public bool PreserveDirectoryStructure { get; set; } = true;
        public bool OverwriteExisting { get; set; } = false;
        public bool EnableValidation { get; set; } = true;
        public bool EncryptAssets { get; set; } = false;
        public bool UpdateAssetDatabase { get; set; } = true;
        public bool SendNotifications { get; set; } = false;
        public bool CleanupTempFiles { get; set; } = true;
        public int MaxConcurrency { get; set; } = 1;
    }

    public class BatchImportOptions;
    {
        public ImportOptions ImportOptions { get; set; } = ImportOptions.Default;
        public int MaxConcurrentImports { get; set; } = 1;
        public int DelayBetweenImports { get; set; } = 0;
        public bool StopOnFirstError { get; set; } = false;
        public string BatchId { get; set; }
    }

    public class BatchImportResult;
    {
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration { get; set; }
        public int TotalFiles { get; set; }
        public int SuccessfulCount { get; set; }
        public int FailedCount { get; set; }
        public List<AssetMetadata> SuccessfulImports { get; set; }
        public List<FailedImport> FailedImports { get; set; }
    }

    public class FailedImport;
    {
        public string SourcePath { get; set; }
        public string ErrorMessage { get; set; }
        public Exception Exception { get; set; }
        public DateTime FailureTime { get; set; } = DateTime.UtcNow;
    }

    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public List<ValidationIssue> Issues { get; set; } = new List<ValidationIssue>();
        public Dictionary<string, object> ValidationData { get; set; } = new Dictionary<string, object>();
    }

    public class ValidationIssue;
    {
        public string Code { get; set; }
        public string Description { get; set; }
        public IssueSeverity Severity { get; set; }
        public string SuggestedFix { get; set; }
    }

    public class ImportConfiguration;
    {
        public int MaxConcurrentImports { get; set; }
        public string DefaultDestination { get; set; }
        public bool EnableCompression { get; set; }
        public CompressionLevel CompressionLevel { get; set; }
        public bool GenerateThumbnails { get; set; }
        public System.Drawing.Size ThumbnailSize { get; set; }
        public bool PreserveDirectoryStructure { get; set; }
        public bool OverwriteExisting { get; set; }
        public bool ValidateOnImport { get; set; }
        public bool BackupOriginal { get; set; }
        public int TemporaryFileRetentionDays { get; set; }
    }

    public enum CompressionLevel;
    {
        None,
        Fast,
        Balanced,
        Maximum;
    }

    public enum IssueSeverity;
    {
        Info,
        Warning,
        Error,
        Critical;
    }

    #endregion;

    #region Custom Exceptions;

    public class AssetImportException : Exception
    {
        public AssetImportException(string message) : base(message) { }
        public AssetImportException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AssetValidationException : Exception
    {
        public AssetValidationException(string message) : base(message) { }
        public AssetValidationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AssetEncryptionException : Exception
    {
        public AssetEncryptionException(string message) : base(message) { }
        public AssetEncryptionException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Built-in Format Handlers (Simplified Implementations)

    internal class FbxFormatHandler : IFormatHandler;
    {
        public Task<ProcessedAsset> ProcessAsync(string sourcePath, string destinationPath, ImportOptions options)
        {
            // Actual FBX processing implementation;
            return Task.FromResult(new ProcessedAsset;
            {
                OriginalPath = sourcePath,
                ProcessedPath = destinationPath,
                FormatVersion = "FBX 2020",
                CompressionRatio = 0.8,
                Metadata = new Dictionary<string, object>
                {
                    ["PolyCount"] = 10000,
                    ["HasAnimation"] = true,
                    ["HasMaterials"] = true;
                }
            });
        }

        public Task<ValidationResult> ValidateAsync(string assetPath, ValidationOptions options = null)
        {
            // FBX validation logic;
            return Task.FromResult(new ValidationResult;
            {
                IsValid = true,
                ValidationData = new Dictionary<string, object>
                {
                    ["Format"] = "FBX",
                    ["Version"] = "2020"
                }
            });
        }

        public Task<Dictionary<string, object>> ExtractMetadataAsync(string assetPath)
        {
            // Extract FBX metadata;
            return Task.FromResult(new Dictionary<string, object>
            {
                ["AssetType"] = "3D Model",
                ["Format"] = "FBX",
                ["Software"] = "Autodesk Maya",
                ["CreationDate"] = DateTime.UtcNow.AddDays(-30)
            });
        }

        public void Dispose()
        {
            // Cleanup resources;
        }
    }

    internal class ImageFormatHandler : IFormatHandler;
    {
        public Task<ProcessedAsset> ProcessAsync(string sourcePath, string destinationPath, ImportOptions options)
        {
            // Image processing implementation;
            return Task.FromResult(new ProcessedAsset;
            {
                OriginalPath = sourcePath,
                ProcessedPath = destinationPath,
                FormatVersion = "1.0",
                CompressionRatio = 0.7,
                Metadata = new Dictionary<string, object>
                {
                    ["Width"] = 1920,
                    ["Height"] = 1080,
                    ["ColorDepth"] = 32,
                    ["HasAlpha"] = true;
                }
            });
        }

        public Task<ValidationResult> ValidateAsync(string assetPath, ValidationOptions options = null)
        {
            // Image validation logic;
            return Task.FromResult(new ValidationResult { IsValid = true });
        }

        public Task<Dictionary<string, object>> ExtractMetadataAsync(string assetPath)
        {
            // Extract image metadata (EXIF, etc.)
            return Task.FromResult(new Dictionary<string, object>
            {
                ["AssetType"] = "Texture",
                ["ColorSpace"] = "sRGB",
                ["Resolution"] = "1920x1080"
            });
        }

        public void Dispose()
        {
            // Cleanup;
        }
    }

    internal class AudioFormatHandler : IFormatHandler;
    {
        public Task<ProcessedAsset> ProcessAsync(string sourcePath, string destinationPath, ImportOptions options)
        {
            // Audio processing implementation;
            return Task.FromResult(new ProcessedAsset;
            {
                OriginalPath = sourcePath,
                ProcessedPath = destinationPath,
                FormatVersion = "1.0",
                CompressionRatio = 0.5,
                Metadata = new Dictionary<string, object>
                {
                    ["Duration"] = 180.5,
                    ["SampleRate"] = 44100,
                    ["Channels"] = 2,
                    ["BitDepth"] = 16;
                }
            });
        }

        public Task<ValidationResult> ValidateAsync(string assetPath, ValidationOptions options = null)
        {
            // Audio validation logic;
            return Task.FromResult(new ValidationResult { IsValid = true });
        }

        public Task<Dictionary<string, object>> ExtractMetadataAsync(string assetPath)
        {
            // Extract audio metadata;
            return Task.FromResult(new Dictionary<string, object>
            {
                ["AssetType"] = "Audio",
                ["Codec"] = "MP3",
                ["Bitrate"] = "320kbps"
            });
        }

        public void Dispose()
        {
            // Cleanup;
        }
    }

    // Additional format handlers would be implemented similarly;

    #endregion;
}
