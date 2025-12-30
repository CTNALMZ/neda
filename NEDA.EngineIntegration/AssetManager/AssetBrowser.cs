using NEDA.Core.Logging;
using NEDA.Core.SystemControl;
using NEDA.EngineIntegration.AssetManager.Interfaces;
using NEDA.EngineIntegration.RenderManager;
using NEDA.EngineIntegration.Unreal;
using NEDA.Logging;
using NEDA.Services;
using NEDA.SystemControl;
using NEDA.UI.Controls;
using NEDA.UI.Windows;
using NEDA.Utilities;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace NEDA.EngineIntegration.AssetManager;
{
    /// <summary>
    /// AssetBrowser sınıfı - Varlık tarama, görüntüleme ve yönetim işlevlerini sağlar;
    /// </summary>
    public class AssetBrowser : INotifyPropertyChanged, IAssetBrowser;
    {
        private readonly ILogger _logger;
        private readonly IFileService _fileService;
        private readonly IAssetImporter _assetImporter;
        private readonly IAssetExporter _assetExporter;
        private readonly IRenderEngine _renderEngine;
        private readonly ISystemManager _systemManager;
        private readonly IUnrealEngine _unrealEngine;

        private AssetItem _selectedAsset;
        private string _currentDirectory;
        private string _searchFilter;
        private AssetViewMode _viewMode;
        private bool _isLoading;
        private AssetFilter _currentFilter;

        /// <summary>
        /// Seçilen varlık;
        /// </summary>
        public AssetItem SelectedAsset;
        {
            get => _selectedAsset;
            set;
            {
                if (_selectedAsset != value)
                {
                    _selectedAsset = value;
                    OnPropertyChanged(nameof(SelectedAsset));
                    OnAssetSelected?.Invoke(this, new AssetSelectedEventArgs(value));
                }
            }
        }

        /// <summary>
        /// Geçerli dizin;
        /// </summary>
        public string CurrentDirectory;
        {
            get => _currentDirectory;
            set;
            {
                if (_currentDirectory != value)
                {
                    _currentDirectory = value;
                    OnPropertyChanged(nameof(CurrentDirectory));
                    _ = LoadAssetsAsync();
                }
            }
        }

        /// <summary>
        /// Arama filtresi;
        /// </summary>
        public string SearchFilter;
        {
            get => _searchFilter;
            set;
            {
                if (_searchFilter != value)
                {
                    _searchFilter = value;
                    OnPropertyChanged(nameof(SearchFilter));
                    ApplyFilter();
                }
            }
        }

        /// <summary>
        Görünüm modu(Grid/List/Thumbnail)
        /// </summary>
        public AssetViewMode ViewMode;
        {
            get => _viewMode;
            set;
            {
                if (_viewMode != value)
                {
                    _viewMode = value;
                    OnPropertyChanged(nameof(ViewMode));
                    OnViewModeChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>
        /// Yükleniyor durumu;
        /// </summary>
        public bool IsLoading;
        {
            get => _isLoading;
            private set;
            {
                if (_isLoading != value)
                {
                    _isLoading = value;
                    OnPropertyChanged(nameof(IsLoading));
                }
            }
        }

        /// <summary>
        /// Filtrelenmiş varlık koleksiyonu;
        /// </summary>
        public ObservableCollection<AssetItem> FilteredAssets { get; private set; }

        /// <summary>
        /// Tüm varlıklar;
        /// </summary>
        public ObservableCollection<AssetItem> AllAssets { get; private set; }

        /// <summary>
        /// Geçerli filtre;
        /// </summary>
        public AssetFilter CurrentFilter;
        {
            get => _currentFilter;
            set;
            {
                if (_currentFilter != value)
                {
                    _currentFilter = value;
                    OnPropertyChanged(nameof(CurrentFilter));
                    ApplyFilter();
                }
            }
        }

        /// <summary>
        /// Desteklenen dosya formatları;
        /// </summary>
        public List<string> SupportedFormats { get; } = new List<string>
        {
            ".fbx", ".obj", ".dae", ".blend", ".max", ".ma", ".mb",
            ".png", ".jpg", ".jpeg", ".tga", ".bmp", ".psd",
            ".wav", ".mp3", ".ogg", ".flac",
            ".mp4", ".avi", ".mov",
            ".material", ".shader", ".prefab", ".scene"
        };

        // Events;
        public event EventHandler<AssetSelectedEventArgs> OnAssetSelected;
        public event EventHandler<AssetsLoadedEventArgs> OnAssetsLoaded;
        public event EventHandler OnViewModeChanged;
        public event EventHandler<AssetOperationEventArgs> OnAssetOperation;
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// AssetBrowser constructor;
        /// </summary>
        public AssetBrowser(
            ILogger logger,
            IFileService fileService,
            IAssetImporter assetImporter,
            IAssetExporter assetExporter,
            IRenderEngine renderEngine,
            ISystemManager systemManager,
            IUnrealEngine unrealEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _assetImporter = assetImporter ?? throw new ArgumentNullException(nameof(assetImporter));
            _assetExporter = assetExporter ?? throw new ArgumentNullException(nameof(assetExporter));
            _renderEngine = renderEngine ?? throw new ArgumentNullException(nameof(renderEngine));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _unrealEngine = unrealEngine ?? throw new ArgumentNullException(nameof(unrealEngine));

            Initialize();
        }

        private void Initialize()
        {
            _logger.LogInformation("AssetBrowser başlatılıyor...");

            FilteredAssets = new ObservableCollection<AssetItem>();
            AllAssets = new ObservableCollection<AssetItem>();
            CurrentFilter = AssetFilter.All;
            ViewMode = AssetViewMode.Thumbnail;

            // Varsayılan dizinleri ayarla;
            _currentDirectory = GetDefaultAssetDirectory();

            _logger.LogInformation("AssetBrowser başlatıldı. Varsayılan dizin: {Directory}", _currentDirectory);
        }

        /// <summary>
        /// Varsayılan varlık dizinini alır;
        /// </summary>
        private string GetDefaultAssetDirectory()
        {
            try
            {
                var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                var nedaAssetsPath = Path.Combine(documentsPath, "NEDA", "Assets");

                if (!Directory.Exists(nedaAssetsPath))
                {
                    Directory.CreateDirectory(nedaAssetsPath);
                }

                return nedaAssetsPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varsayılan varlık dizini alınamadı");
                return Directory.GetCurrentDirectory();
            }
        }

        /// <summary>
        /// Varlıkları asenkron olarak yükler;
        /// </summary>
        public async Task LoadAssetsAsync()
        {
            if (string.IsNullOrWhiteSpace(CurrentDirectory) || !Directory.Exists(CurrentDirectory))
            {
                _logger.LogWarning("Geçersiz dizin: {Directory}", CurrentDirectory);
                return;
            }

            try
            {
                IsLoading = true;
                _logger.LogInformation("Varlıklar yükleniyor: {Directory}", CurrentDirectory);

                await Task.Run(() =>
                {
                    AllAssets.Clear();
                    var files = Directory.GetFiles(CurrentDirectory, "*.*", SearchOption.AllDirectories)
                        .Where(IsSupportedFormat)
                        .ToList();

                    foreach (var file in files)
                    {
                        try
                        {
                            var asset = CreateAssetItem(file);
                            if (asset != null)
                            {
                                Application.Current.Dispatcher.Invoke(() => AllAssets.Add(asset));
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Varlık oluşturma hatası: {File}", file);
                        }
                    }
                });

                ApplyFilter();

                OnAssetsLoaded?.Invoke(this, new AssetsLoadedEventArgs(AllAssets.Count));
                _logger.LogInformation("{Count} varlık başarıyla yüklendi", AllAssets.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varlıklar yüklenirken hata oluştu");
                OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                    "Load Assets Failed",
                    ex.Message,
                    AssetOperationType.Error));
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Varlık öğesi oluşturur;
        /// </summary>
        private AssetItem CreateAssetItem(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);
                var extension = fileInfo.Extension.ToLower();
                var assetType = GetAssetType(extension);

                var thumbnail = GenerateThumbnail(filePath, assetType);

                return new AssetItem;
                {
                    Id = Guid.NewGuid(),
                    Name = fileInfo.Name,
                    FullPath = filePath,
                    Directory = fileInfo.DirectoryName,
                    FileSize = fileInfo.Length,
                    FileExtension = extension,
                    AssetType = assetType,
                    Thumbnail = thumbnail,
                    CreatedDate = fileInfo.CreationTime,
                    ModifiedDate = fileInfo.LastWriteTime,
                    ImportStatus = ImportStatus.NotImported,
                    Metadata = new AssetMetadata;
                    {
                        Author = Environment.UserName,
                        Version = "1.0",
                        Tags = new List<string>()
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Varlık öğesi oluşturulamadı: {FilePath}", filePath);
                return null;
            }
        }

        /// <summary>
        /// Dosya formatı destekleniyor mu kontrol eder;
        /// </summary>
        private bool IsSupportedFormat(string filePath)
        {
            var extension = Path.GetExtension(filePath).ToLower();
            return SupportedFormats.Contains(extension);
        }

        /// <summary>
        /// Dosya uzantısına göre varlık türünü belirler;
        /// </summary>
        private AssetType GetAssetType(string extension)
        {
            return extension switch;
            {
                ".fbx" or ".obj" or ".dae" or ".blend" or ".max" or ".ma" or ".mb" => AssetType.Model3D,
                ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".psd" => AssetType.Texture,
                ".wav" or ".mp3" or ".ogg" or ".flac" => AssetType.Audio,
                ".mp4" or ".avi" or ".mov" => AssetType.Video,
                ".material" or ".shader" => AssetType.Material,
                ".prefab" => AssetType.Prefab,
                ".scene" => AssetType.Scene,
                _ => AssetType.Other;
            };
        }

        /// <summary>
        /// Küçük resim oluşturur;
        /// </summary>
        private byte[] GenerateThumbnail(string filePath, AssetType assetType)
        {
            try
            {
                // Gerçek implementasyonda render engine kullanılarak thumbnail oluşturulacak;
                // Şimdilik placeholder implementasyon;
                return assetType switch;
                {
                    AssetType.Model3D => GenerateModelThumbnail(filePath),
                    AssetType.Texture => GenerateTextureThumbnail(filePath),
                    AssetType.Audio => GenerateAudioThumbnail(filePath),
                    _ => GenerateDefaultThumbnail(assetType)
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Thumbnail oluşturulamadı: {FilePath}", filePath);
                return GenerateDefaultThumbnail(assetType);
            }
        }

        private byte[] GenerateModelThumbnail(string filePath)
        {
            // Model thumbnail için render engine kullan;
            return _renderEngine.GenerateThumbnail(filePath, 128, 128);
        }

        private byte[] GenerateTextureThumbnail(string filePath)
        {
            try
            {
                using var image = System.Drawing.Image.FromFile(filePath);
                using var thumb = image.GetThumbnailImage(128, 128, null, IntPtr.Zero);
                using var ms = new MemoryStream();
                thumb.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch
            {
                return GenerateDefaultThumbnail(AssetType.Texture);
            }
        }

        private byte[] GenerateAudioThumbnail(string filePath)
        {
            // Audio dosyaları için waveform thumbnail;
            return GenerateDefaultThumbnail(AssetType.Audio);
        }

        private byte[] GenerateDefaultThumbnail(AssetType assetType)
        {
            // Default thumbnail assets;
            return Array.Empty<byte>();
        }

        /// <summary>
        /// Filtre uygular;
        /// </summary>
        private void ApplyFilter()
        {
            try
            {
                FilteredAssets.Clear();

                var filtered = AllAssets.AsEnumerable();

                // Asset type filter;
                if (CurrentFilter != AssetFilter.All)
                {
                    filtered = filtered.Where(a => a.AssetType == (AssetType)CurrentFilter);
                }

                // Search filter;
                if (!string.IsNullOrWhiteSpace(SearchFilter))
                {
                    filtered = filtered.Where(a =>
                        a.Name.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase) ||
                        (a.Metadata?.Tags?.Any(t => t.Contains(SearchFilter, StringComparison.OrdinalIgnoreCase)) ?? false));
                }

                // Sort by name;
                filtered = filtered.OrderBy(a => a.Name);

                foreach (var asset in filtered)
                {
                    FilteredAssets.Add(asset);
                }

                _logger.LogDebug("Filtre uygulandı. {Filtered}/{Total} varlık gösteriliyor",
                    FilteredAssets.Count, AllAssets.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Filtre uygulanırken hata oluştu");
            }
        }

        /// <summary>
        /// Varlığı içe aktarır;
        /// </summary>
        public async Task<ImportResult> ImportAssetAsync(string filePath, ImportOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                throw new FileNotFoundException("Dosya bulunamadı", filePath);
            }

            try
            {
                IsLoading = true;
                _logger.LogInformation("Varlık içe aktarılıyor: {FilePath}", filePath);

                var result = await _assetImporter.ImportAsync(filePath, options ?? new ImportOptions());

                if (result.Success)
                {
                    // Yeni varlığı listeye ekle;
                    var asset = CreateAssetItem(result.ImportedPath);
                    if (asset != null)
                    {
                        AllAssets.Add(asset);
                        ApplyFilter();

                        OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                            "Import Successful",
                            $"{Path.GetFileName(filePath)} başarıyla içe aktarıldı",
                            AssetOperationType.Success));
                    }
                }
                else;
                {
                    _logger.LogError("Varlık içe aktarma başarısız: {Error}", result.ErrorMessage);
                    OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                        "Import Failed",
                        result.ErrorMessage,
                        AssetOperationType.Error));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varlık içe aktarma hatası");
                throw;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Varlığı dışa aktarır;
        /// </summary>
        public async Task<ExportResult> ExportAssetAsync(AssetItem asset, string format, string outputPath)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            try
            {
                IsLoading = true;
                _logger.LogInformation("Varlık dışa aktarılıyor: {Asset} -> {Format}", asset.Name, format);

                var result = await _assetExporter.ExportAsync(asset.FullPath, format, outputPath);

                if (result.Success)
                {
                    OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                        "Export Successful",
                        $"{asset.Name} başarıyla {format} formatında dışa aktarıldı",
                        AssetOperationType.Success));
                }
                else;
                {
                    OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                        "Export Failed",
                        result.ErrorMessage,
                        AssetOperationType.Error));
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varlık dışa aktarma hatası");
                throw;
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Varlığı Unreal Engine projesine ekler;
        /// </summary>
        public async Task<bool> AddToUnrealProjectAsync(AssetItem asset, string unrealProjectPath)
        {
            if (asset == null || string.IsNullOrWhiteSpace(unrealProjectPath))
            {
                throw new ArgumentException("Geçersiz parametreler");
            }

            try
            {
                _logger.LogInformation("Varlık Unreal Engine projesine ekleniyor: {Asset}", asset.Name);

                var success = await _unrealEngine.ImportAssetAsync(asset.FullPath, unrealProjectPath);

                if (success)
                {
                    asset.ImportStatus = ImportStatus.ImportedToUnreal;
                    OnPropertyChanged(nameof(SelectedAsset));

                    OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                        "Unreal Import Successful",
                        $"{asset.Name} Unreal Engine projesine başarıyla eklendi",
                        AssetOperationType.Success));
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unreal Engine'e ekleme hatası");
                throw;
            }
        }

        /// <summary>
        /// Varlığı önizle;
        /// </summary>
        public void PreviewAsset(AssetItem asset)
        {
            if (asset == null) return;

            try
            {
                _logger.LogDebug("Varlık önizleniyor: {Asset}", asset.Name);

                // Preview window aç veya render engine ile preview göster;
                _renderEngine.PreviewAsset(asset.FullPath, asset.AssetType);

                OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                    "Preview",
                    $"{asset.Name} önizleniyor",
                    AssetOperationType.Info));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Varlık önizleme hatası");
                throw;
            }
        }

        /// <summary>
        /// Varlıkları toplu işlem yapar;
        /// </summary>
        public async Task<BatchOperationResult> BatchProcessAsync(
            IEnumerable<AssetItem> assets,
            BatchOperation operation,
            BatchOptions options)
        {
            if (assets == null || !assets.Any())
            {
                throw new ArgumentException("En az bir varlık seçilmelidir");
            }

            try
            {
                IsLoading = true;
                _logger.LogInformation("Toplu işlem başlatılıyor: {Operation} - {Count} varlık",
                    operation, assets.Count());

                var result = new BatchOperationResult();

                foreach (var asset in assets)
                {
                    try
                    {
                        switch (operation)
                        {
                            case BatchOperation.ConvertFormat:
                                await ConvertAssetFormatAsync(asset, options.TargetFormat, options.OutputDirectory);
                                result.SuccessCount++;
                                break;
                            case BatchOperation.ResizeTextures:
                                await ResizeTextureAsync(asset, options.Width, options.Height);
                                result.SuccessCount++;
                                break;
                            case BatchOperation.OptimizeModels:
                                await OptimizeModelAsync(asset, options.OptimizationLevel);
                                result.SuccessCount++;
                                break;
                            case BatchOperation.AddMetadata:
                                await AddMetadataAsync(asset, options.Metadata);
                                result.SuccessCount++;
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Varlık işlenirken hata: {Asset}", asset.Name);
                        result.FailedItems.Add(new FailedItem;
                        {
                            Asset = asset,
                            Error = ex.Message;
                        });
                    }
                }

                result.Success = result.FailedItems.Count == 0;

                OnAssetOperation?.Invoke(this, new AssetOperationEventArgs(
                    "Batch Process Completed",
                    $"{result.SuccessCount} varlık başarıyla işlendi, {result.FailedItems.Count} başarısız",
                    result.Success ? AssetOperationType.Success : AssetOperationType.Warning));

                return result;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ConvertAssetFormatAsync(AssetItem asset, string targetFormat, string outputDir)
        {
            // Format dönüştürme implementasyonu;
            await Task.Delay(100); // Placeholder;
        }

        private async Task ResizeTextureAsync(AssetItem asset, int width, int height)
        {
            // Texture resize implementasyonu;
            await Task.Delay(100); // Placeholder;
        }

        private async Task OptimizeModelAsync(AssetItem asset, OptimizationLevel level)
        {
            // Model optimization implementasyonu;
            await Task.Delay(100); // Placeholder;
        }

        private async Task AddMetadataAsync(AssetItem asset, Dictionary<string, string> metadata)
        {
            if (asset.Metadata == null)
            {
                asset.Metadata = new AssetMetadata();
            }

            foreach (var kvp in metadata)
            {
                asset.Metadata.CustomProperties[kvp.Key] = kvp.Value;
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Dizin gezgini aç;
        /// </summary>
        public bool BrowseForDirectory()
        {
            try
            {
                using var dialog = new System.Windows.Forms.FolderBrowserDialog;
                {
                    Description = "Varlık dizinini seçin",
                    SelectedPath = CurrentDirectory,
                    ShowNewFolderButton = true;
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    CurrentDirectory = dialog.SelectedPath;
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Dizin seçme hatası");
                return false;
            }
        }

        /// <summary>
        /// Özellik değişikliği event'i;
        /// </summary>
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Dispose pattern;
        /// </summary>
        public void Dispose()
        {
            _logger.LogInformation("AssetBrowser kapatılıyor...");
            FilteredAssets.Clear();
            AllAssets.Clear();
            GC.SuppressFinalize(this);
        }
    }

    #region Supporting Types;

    /// <summary>
    /// Varlık türleri;
    /// </summary>
    public enum AssetType;
    {
        Model3D,
        Texture,
        Audio,
        Video,
        Material,
        Prefab,
        Scene,
        Script,
        Other;
    }

    /// <summary>
    /// Varlık filtreleri;
    /// </summary>
    public enum AssetFilter;
    {
        All = 0,
        Model3D = AssetType.Model3D,
        Texture = AssetType.Texture,
        Audio = AssetType.Audio,
        Video = AssetType.Video,
        Material = AssetType.Material;
    }

    /// <summary>
    /// Görünüm modları;
    /// </summary>
    public enum AssetViewMode;
    {
        Thumbnail,
        Grid,
        List,
        Details;
    }

    /// <summary>
    /// İçe aktarma durumu;
    /// </summary>
    public enum ImportStatus;
    {
        NotImported,
        Imported,
        ImportedToUnreal,
        Failed;
    }

    /// <summary>
    /// Toplu işlem türleri;
    /// </summary>
    public enum BatchOperation;
    {
        ConvertFormat,
        ResizeTextures,
        OptimizeModels,
        AddMetadata,
        GenerateThumbnails;
    }

    /// <summary>
    /// Optimizasyon seviyeleri;
    /// </summary>
    public enum OptimizationLevel;
    {
        Low,
        Medium,
        High,
        Ultra;
    }

    /// <summary>
    /// Varlık operasyon türleri;
    /// </summary>
    public enum AssetOperationType;
    {
        Info,
        Success,
        Warning,
        Error;
    }

    /// <summary>
    /// Varlık öğesi sınıfı;
    /// </summary>
    public class AssetItem;
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string Directory { get; set; }
        public long FileSize { get; set; }
        public string FileExtension { get; set; }
        public AssetType AssetType { get; set; }
        public byte[] Thumbnail { get; set; }
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public ImportStatus ImportStatus { get; set; }
        public AssetMetadata Metadata { get; set; }

        public string FileSizeFormatted => FormatFileSize(FileSize);
        public string TypeIcon => GetTypeIcon();

        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len /= 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }

        private string GetTypeIcon()
        {
            return AssetType switch;
            {
                AssetType.Model3D => "📦",
                AssetType.Texture => "🖼️",
                AssetType.Audio => "🎵",
                AssetType.Video => "🎬",
                AssetType.Material => "🎨",
                AssetType.Prefab => "⚙️",
                AssetType.Scene => "🏞️",
                _ => "📄"
            };
        }
    }

    /// <summary>
    /// Varlık metadata sınıfı;
    /// </summary>
    public class AssetMetadata;
    {
        public string Author { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, string> CustomProperties { get; set; }

        public AssetMetadata()
        {
            Tags = new List<string>();
            CustomProperties = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// İçe aktarma seçenekleri;
    /// </summary>
    public class ImportOptions;
    {
        public bool GenerateThumbnail { get; set; } = true;
        public bool PreserveHierarchy { get; set; } = true;
        public string TargetDirectory { get; set; }
        public Dictionary<string, object> ImportSettings { get; set; }

        public ImportOptions()
        {
            ImportSettings = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// İçe aktarma sonucu;
    /// </summary>
    public class ImportResult;
    {
        public bool Success { get; set; }
        public string ImportedPath { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Dışa aktarma sonucu;
    /// </summary>
    public class ExportResult;
    {
        public bool Success { get; set; }
        public string ExportedPath { get; set; }
        public string ErrorMessage { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Toplu işlem seçenekleri;
    /// </summary>
    public class BatchOptions;
    {
        public string TargetFormat { get; set; }
        public string OutputDirectory { get; set; }
        public int Width { get; set; } = 1024;
        public int Height { get; set; } = 1024;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public Dictionary<string, string> Metadata { get; set; }

        public BatchOptions()
        {
            Metadata = new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// Toplu işlem sonucu;
    /// </summary>
    public class BatchOperationResult;
    {
        public bool Success { get; set; }
        public int SuccessCount { get; set; }
        public List<FailedItem> FailedItems { get; set; }
        public TimeSpan TotalDuration { get; set; }

        public BatchOperationResult()
        {
            FailedItems = new List<FailedItem>();
        }
    }

    /// <summary>
    /// Başarısız öğe;
    /// </summary>
    public class FailedItem;
    {
        public AssetItem Asset { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Varlık seçim event args;
    /// </summary>
    public class AssetSelectedEventArgs : EventArgs;
    {
        public AssetItem SelectedAsset { get; }

        public AssetSelectedEventArgs(AssetItem asset)
        {
            SelectedAsset = asset;
        }
    }

    /// <summary>
    /// Varlıklar yüklendi event args;
    /// </summary>
    public class AssetsLoadedEventArgs : EventArgs;
    {
        public int AssetCount { get; }

        public AssetsLoadedEventArgs(int count)
        {
            AssetCount = count;
        }
    }

    /// <summary>
    /// Varlık operasyon event args;
    /// </summary>
    public class AssetOperationEventArgs : EventArgs;
    {
        public string Operation { get; }
        public string Message { get; }
        public AssetOperationType Type { get; }
        public DateTime Timestamp { get; }

        public AssetOperationEventArgs(string operation, string message, AssetOperationType type)
        {
            Operation = operation;
            Message = message;
            Type = type;
            Timestamp = DateTime.Now;
        }
    }

    #endregion;
}
