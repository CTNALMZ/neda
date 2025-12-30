using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring;
using NEDA.Core.Configuration.AppSettings;

namespace NEDA.Animation.AnimationTools.MotionImport;
{
    /// <summary>
    /// Animation format conversion sistemi - Çeşitli formatlar arasında animasyon dönüşümü yapar;
    /// </summary>
    public interface IAnimationConverter;
    {
        /// <summary>
        /// Animasyon formatını dönüştür;
        /// </summary>
        Task<ConversionResult> ConvertAnimationAsync(ConversionRequest request);

        /// <summary>
        /// Batch animasyon dönüşümü;
        /// </summary>
        Task<BatchConversionResult> BatchConvertAsync(BatchConversionRequest request);

        /// <summary>
        /// FBX animasyonunu import et;
        /// </summary>
        Task<ImportResult> ImportFbxAnimationAsync(FbxImportRequest request);

        /// <summary>
        /// GLTF animasyonunu import et;
        /// </summary>
        Task<ImportResult> ImportGltfAnimationAsync(GltfImportRequest request);

        /// <summary>
        /// Collada animasyonunu import et;
        /// </summary>
        Task<ImportResult> ImportColladaAnimationAsync(ColladaImportRequest request);

        /// <summary>
        /// BVH animasyonunu import et;
        /// </summary>
        Task<ImportResult> ImportBvhAnimationAsync(BvhImportRequest request);

        /// <summary>
        /// Mixamo animasyonunu import et;
        /// </summary>
        Task<ImportResult> ImportMixamoAnimationAsync(MixamoImportRequest request);

        /// <summary>
        /// Unity animasyonunu export et;
        /// </summary>
        Task<ExportResult> ExportToUnityAsync(UnityExportRequest request);

        /// <summary>
        /// Unreal Engine animasyonunu export et;
        /// </summary>
        Task<ExportResult> ExportToUnrealAsync(UnrealExportRequest request);

        /// <summary>
        /// Godot animasyonunu export et;
        /// </summary>
        Task<ExportResult> ExportToGodotAsync(GodotExportRequest request);

        /// <summary>
        /// NEDA animasyon formatını export et;
        /// </summary>
        Task<ExportResult> ExportToNedaFormatAsync(NedaExportRequest request);

        /// <summary>
        /// Retarget animasyonu farklı skeleton'a;
        /// </summary>
        Task<RetargetResult> RetargetAnimationAsync(RetargetRequest request);

        /// <summary>
        /// Animasyon optimizasyonu uygula;
        /// </summary>
        Task<OptimizationResult> OptimizeAnimationAsync(AnimationOptimizationRequest request);

        /// <summary>
        /// Frame rate conversion uygula;
        /// </summary>
        Task<FrameRateResult> ConvertFrameRateAsync(FrameRateConversionRequest request);

        /// <summary>
        /// Animasyon compression uygula;
        /// </summary>
        Task<CompressionResult> CompressAnimationAsync(CompressionRequest request);

        /// <summary>
        /// Animation baking uygula;
        /// </summary>
        Task<BakingResult> BakeAnimationAsync(BakingRequest request);

        /// <summary>
        /// Animation mirroring uygula;
        /// </summary>
        Task<MirroringResult> MirrorAnimationAsync(MirroringRequest request);

        /// <summary>
        /// Animation looping uygula;
        /// </summary>
        Task<LoopingResult> MakeAnimationLoopAsync(LoopingRequest request);

        /// <summary>
        /// Animation splitting uygula;
        /// </summary>
        Task<SplittingResult> SplitAnimationAsync(SplittingRequest request);

        /// <summary>
        /// Animation merging uygula;
        /// </summary>
        Task<MergingResult> MergeAnimationsAsync(MergingRequest request);

        /// <summary>
        /// Animation preview oluştur;
        /// </summary>
        Task<PreviewResult> CreateAnimationPreviewAsync(PreviewRequest request);

        /// <summary>
        /// Conversion pipeline oluştur;
        /// </summary>
        Task<ConversionPipeline> CreateConversionPipelineAsync(PipelineDefinition definition);

        /// <summary>
        /// Supported formatları getir;
        /// </summary>
        Task<List<SupportedFormat>> GetSupportedFormatsAsync();

        /// <summary>
        /// Format detaylarını getir;
        /// </summary>
        Task<FormatDetails> GetFormatDetailsAsync(string format);

        /// <summary>
        /// Conversion istatistiklerini getir;
        /// </summary>
        Task<ConversionStatistics> GetConversionStatisticsAsync();

        /// <summary>
        /// Cache'i temizle;
        /// </summary>
        Task ClearConversionCacheAsync();
    }

    /// <summary>
    /// Animation converter implementasyonu;
    /// </summary>
    public class AnimationConverter : IAnimationConverter, IDisposable;
    {
        private readonly IAnimationRepository _animationRepository;
        private readonly ISkeletonManager _skeletonManager;
        private readonly IRetargetingEngine _retargetingEngine;
        private readonly IOptimizationEngine _optimizationEngine;
        private readonly IFileService _fileService;
        private readonly ILogger<AnimationConverter> _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ICacheManager _cacheManager;
        private readonly AppConfig _appConfig;
        private readonly Dictionary<string, IFormatConverter> _formatConverters;
        private readonly object _syncLock = new object();
        private bool _disposed = false;

        public AnimationConverter(
            IAnimationRepository animationRepository,
            ISkeletonManager skeletonManager,
            IRetargetingEngine retargetingEngine,
            IOptimizationEngine optimizationEngine,
            IFileService fileService,
            ILogger<AnimationConverter> logger,
            IErrorReporter errorReporter,
            IPerformanceMonitor performanceMonitor,
            ICacheManager cacheManager,
            AppConfig appConfig)
        {
            _animationRepository = animationRepository ?? throw new ArgumentNullException(nameof(animationRepository));
            _skeletonManager = skeletonManager ?? throw new ArgumentNullException(nameof(skeletonManager));
            _retargetingEngine = retargetingEngine ?? throw new ArgumentNullException(nameof(retargetingEngine));
            _optimizationEngine = optimizationEngine ?? throw new ArgumentNullException(nameof(optimizationEngine));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            // Format converter'ları initialize et;
            _formatConverters = InitializeFormatConverters();
        }

        /// <summary>
        /// Animasyon formatını dönüştür;
        /// </summary>
        public async Task<ConversionResult> ConvertAnimationAsync(ConversionRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ConvertAnimation");

            try
            {
                _logger.LogInformation($"Converting animation from {request.SourceFormat} to {request.TargetFormat}");

                ValidateConversionRequest(request);

                // Cache'den kontrol et;
                var cacheKey = CreateConversionCacheKey(request);
                var cachedResult = await _cacheManager.GetAsync<ConversionResult>(cacheKey);
                if (cachedResult != null)
                {
                    _logger.LogDebug($"Cache hit for animation conversion: {cacheKey}");
                    cachedResult.FromCache = true;
                    return cachedResult;
                }

                // Source animasyonu yükle;
                var sourceAnimation = await LoadSourceAnimationAsync(request);

                // Format converter seç;
                var converter = GetFormatConverter(request.SourceFormat, request.TargetFormat);
                if (converter == null)
                {
                    throw new NEDAException(ErrorCode.FormatConversionNotSupported,
                        $"Conversion from {request.SourceFormat} to {request.TargetFormat} is not supported");
                }

                // Dönüşümü uygula;
                var convertedData = await converter.ConvertAsync(sourceAnimation, request.Options);

                // Optimizasyon uygula;
                if (request.Options?.EnableOptimization == true)
                {
                    convertedData = await ApplyOptimizationAsync(convertedData, request.Options.OptimizationLevel);
                }

                // Compression uygula;
                if (request.Options?.EnableCompression == true)
                {
                    convertedData = await ApplyCompressionAsync(convertedData, request.Options.CompressionLevel);
                }

                // Sonucu kaydet;
                var result = await SaveConvertedAnimationAsync(convertedData, request);

                // Cache'e kaydet;
                await CacheConversionResultAsync(cacheKey, result);

                // İstatistikleri güncelle;
                await UpdateConversionStatisticsAsync(request.SourceFormat, request.TargetFormat, true);

                _logger.LogInformation($"Animation converted successfully. Result ID: {result.ConvertedAnimationId}");
                return result;
            }
            catch (NEDAException)
            {
                await UpdateConversionStatisticsAsync(request.SourceFormat, request.TargetFormat, false);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error converting animation from {request.SourceFormat} to {request.TargetFormat}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                await UpdateConversionStatisticsAsync(request.SourceFormat, request.TargetFormat, false);
                throw new NEDAException(ErrorCode.AnimationConversionFailed, "Failed to convert animation", ex);
            }
        }

        /// <summary>
        /// Batch animasyon dönüşümü;
        /// </summary>
        public async Task<BatchConversionResult> BatchConvertAsync(BatchConversionRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("BatchConvert");

            try
            {
                _logger.LogInformation($"Batch converting {request.Conversions.Count} animations");

                if (request.Conversions == null || request.Conversions.Count == 0)
                {
                    throw new NEDAException(ErrorCode.InvalidBatchRequest, "No conversion requests provided");
                }

                var results = new List<ConversionResult>();
                var failedConversions = new List<FailedConversion>();

                // Paralel veya seri conversion;
                if (request.ParallelProcessing && _appConfig.AnimationConverter.MaxParallelConversions > 1)
                {
                    await ProcessBatchInParallelAsync(request, results, failedConversions);
                }
                else;
                {
                    await ProcessBatchInSequenceAsync(request, results, failedConversions);
                }

                var batchResult = new BatchConversionResult;
                {
                    TotalConversions = request.Conversions.Count,
                    SuccessfulConversions = results.Count,
                    FailedConversions = failedConversions.Count,
                    Results = results,
                    FailedItems = failedConversions,
                    TotalProcessingTime = stopwatch.StopAndGetMetrics().ExecutionTime;
                };

                _logger.LogInformation($"Batch conversion completed: {results.Count} successful, {failedConversions.Count} failed");
                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in batch conversion");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.BatchConversionFailed, "Failed to perform batch conversion", ex);
            }
        }

        /// <summary>
        /// FBX animasyonunu import et;
        /// </summary>
        public async Task<ImportResult> ImportFbxAnimationAsync(FbxImportRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ImportFbxAnimation");

            try
            {
                _logger.LogInformation($"Importing FBX animation: {request.FilePath}");

                ValidateFileExists(request.FilePath);

                // FBX parser'ı kullan;
                var fbxParser = new FbxParser();
                var fbxData = await fbxParser.ParseAsync(request.FilePath);

                // Animasyon verilerini çıkar;
                var animationData = await ExtractFbxAnimationDataAsync(fbxData, request);

                // Skeleton mapping uygula;
                if (request.TargetSkeletonId != null)
                {
                    animationData = await ApplySkeletonMappingAsync(animationData, request.TargetSkeletonId);
                }

                // Optimizasyon uygula;
                if (request.EnableOptimization)
                {
                    animationData = await OptimizeImportedAnimationAsync(animationData, request.OptimizationLevel);
                }

                // Animasyonu kaydet;
                var savedAnimation = await SaveImportedAnimationAsync(animationData, request);

                var result = new ImportResult;
                {
                    Success = true,
                    ImportedAnimationId = savedAnimation.Id,
                    ImportStatistics = new ImportStatistics;
                    {
                        FrameCount = animationData.Frames.Count,
                        Duration = animationData.Duration,
                        BoneCount = animationData.BoneCount,
                        FileSize = await _fileService.GetFileSizeAsync(request.FilePath),
                        ImportTime = stopwatch.StopAndGetMetrics().ExecutionTime;
                    },
                    Warnings = animationData.Warnings;
                };

                _logger.LogInformation($"FBX animation imported successfully. Animation ID: {savedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error importing FBX animation: {request.FilePath}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.FbxImportFailed, "Failed to import FBX animation", ex);
            }
        }

        /// <summary>
        /// GLTF animasyonunu import et;
        /// </summary>
        public async Task<ImportResult> ImportGltfAnimationAsync(GltfImportRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ImportGltfAnimation");

            try
            {
                _logger.LogInformation($"Importing GLTF animation: {request.FilePath}");

                ValidateFileExists(request.FilePath);

                // GLTF parser'ı kullan;
                var gltfParser = new GltfParser();
                var gltfData = await gltfParser.ParseAsync(request.FilePath);

                // Animasyon verilerini çıkar;
                var animationData = await ExtractGltfAnimationDataAsync(gltfData, request);

                // Binary veya embedded data işle;
                if (request.ProcessBinaryData)
                {
                    animationData = await ProcessGltfBinaryDataAsync(animationData, gltfData);
                }

                // Animasyonu kaydet;
                var savedAnimation = await SaveImportedAnimationAsync(animationData, request);

                var result = new ImportResult;
                {
                    Success = true,
                    ImportedAnimationId = savedAnimation.Id,
                    ImportStatistics = new ImportStatistics;
                    {
                        FrameCount = animationData.Frames.Count,
                        Duration = animationData.Duration,
                        BoneCount = animationData.BoneCount,
                        FileSize = await _fileService.GetFileSizeAsync(request.FilePath),
                        ImportTime = stopwatch.StopAndGetMetrics().ExecutionTime;
                    }
                };

                _logger.LogInformation($"GLTF animation imported successfully. Animation ID: {savedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error importing GLTF animation: {request.FilePath}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.GltfImportFailed, "Failed to import GLTF animation", ex);
            }
        }

        /// <summary>
        /// Collada animasyonunu import et;
        /// </summary>
        public async Task<ImportResult> ImportColladaAnimationAsync(ColladaImportRequest request)
        {
            try
            {
                _logger.LogInformation($"Importing Collada animation: {request.FilePath}");

                ValidateFileExists(request.FilePath);

                // Collada parser'ı kullan;
                var colladaParser = new ColladaParser();
                var colladaData = await colladaParser.ParseAsync(request.FilePath);

                // Animasyon verilerini çıkar;
                var animationData = await ExtractColladaAnimationDataAsync(colladaData, request);

                // Transform hierarchy'yi işle;
                animationData = await ProcessColladaHierarchyAsync(animationData, colladaData);

                // Animasyonu kaydet;
                var savedAnimation = await SaveImportedAnimationAsync(animationData, request);

                var result = new ImportResult;
                {
                    Success = true,
                    ImportedAnimationId = savedAnimation.Id;
                };

                _logger.LogInformation($"Collada animation imported successfully. Animation ID: {savedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error importing Collada animation: {request.FilePath}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.ColladaImportFailed, "Failed to import Collada animation", ex);
            }
        }

        /// <summary>
        /// BVH animasyonunu import et;
        /// </summary>
        public async Task<ImportResult> ImportBvhAnimationAsync(BvhImportRequest request)
        {
            try
            {
                _logger.LogInformation($"Importing BVH animation: {request.FilePath}");

                ValidateFileExists(request.FilePath);

                // BVH parser'ı kullan;
                var bvhParser = new BvhParser();
                var bvhData = await bvhParser.ParseAsync(request.FilePath);

                // Hierarchical data'yı işle;
                var animationData = await ProcessBvhHierarchyAsync(bvhData, request);

                // Rotation order'ı düzelt;
                animationData = await FixBvhRotationOrderAsync(animationData);

                // Scale factor uygula;
                if (request.ScaleFactor != 1.0f)
                {
                    animationData = await ApplyScaleFactorAsync(animationData, request.ScaleFactor);
                }

                // Animasyonu kaydet;
                var savedAnimation = await SaveImportedAnimationAsync(animationData, request);

                var result = new ImportResult;
                {
                    Success = true,
                    ImportedAnimationId = savedAnimation.Id;
                };

                _logger.LogInformation($"BVH animation imported successfully. Animation ID: {savedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error importing BVH animation: {request.FilePath}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.BvhImportFailed, "Failed to import BVH animation", ex);
            }
        }

        /// <summary>
        /// Mixamo animasyonunu import et;
        /// </summary>
        public async Task<ImportResult> ImportMixamoAnimationAsync(MixamoImportRequest request)
        {
            try
            {
                _logger.LogInformation($"Importing Mixamo animation: {request.FilePath}");

                ValidateFileExists(request.FilePath);

                // Mixamo özel işlemleri;
                var animationData = await ProcessMixamoAnimationAsync(request);

                // T-pose alignment;
                if (request.AlignToTPose)
                {
                    animationData = await AlignToTPoseAsync(animationData);
                }

                // Root motion extraction;
                if (request.ExtractRootMotion)
                {
                    animationData = await ExtractRootMotionAsync(animationData);
                }

                // Animasyonu kaydet;
                var savedAnimation = await SaveImportedAnimationAsync(animationData, request);

                var result = new ImportResult;
                {
                    Success = true,
                    ImportedAnimationId = savedAnimation.Id,
                    MixamoCompatibility = CheckMixamoCompatibility(animationData)
                };

                _logger.LogInformation($"Mixamo animation imported successfully. Animation ID: {savedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error importing Mixamo animation: {request.FilePath}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.MixamoImportFailed, "Failed to import Mixamo animation", ex);
            }
        }

        /// <summary>
        /// Unity animasyonunu export et;
        /// </summary>
        public async Task<ExportResult> ExportToUnityAsync(UnityExportRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("ExportToUnity");

            try
            {
                _logger.LogInformation($"Exporting animation {request.AnimationId} to Unity format");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Unity formatına dönüştür;
                var unityData = await ConvertToUnityFormatAsync(animation, request);

                // FBX veya anim dosyası oluştur;
                byte[] exportData;
                if (request.ExportAsFbx)
                {
                    exportData = await CreateUnityFbxFileAsync(unityData, request);
                }
                else;
                {
                    exportData = await CreateUnityAnimFileAsync(unityData, request);
                }

                // Dosyayı kaydet;
                var filePath = await SaveExportFileAsync(exportData, request.OutputPath, "unity");

                var result = new ExportResult;
                {
                    Success = true,
                    FilePath = filePath,
                    FileSize = exportData.Length,
                    ExportFormat = request.ExportAsFbx ? "FBX" : "Unity Anim",
                    ExportTime = stopwatch.StopAndGetMetrics().ExecutionTime;
                };

                _logger.LogInformation($"Animation exported to Unity successfully: {filePath}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error exporting animation to Unity: {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.UnityExportFailed, "Failed to export animation to Unity", ex);
            }
        }

        /// <summary>
        /// Unreal Engine animasyonunu export et;
        /// </summary>
        public async Task<ExportResult> ExportToUnrealAsync(UnrealExportRequest request)
        {
            try
            {
                _logger.LogInformation($"Exporting animation {request.AnimationId} to Unreal Engine format");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Unreal formatına dönüştür;
                var unrealData = await ConvertToUnrealFormatAsync(animation, request);

                // FBX dosyası oluştur;
                var exportData = await CreateUnrealFbxFileAsync(unrealData, request);

                // Dosyayı kaydet;
                var filePath = await SaveExportFileAsync(exportData, request.OutputPath, "unreal");

                var result = new ExportResult;
                {
                    Success = true,
                    FilePath = filePath,
                    FileSize = exportData.Length,
                    ExportFormat = "FBX",
                    UnrealCompatibility = CheckUnrealCompatibility(unrealData)
                };

                _logger.LogInformation($"Animation exported to Unreal successfully: {filePath}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error exporting animation to Unreal: {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.UnrealExportFailed, "Failed to export animation to Unreal", ex);
            }
        }

        /// <summary>
        /// Godot animasyonunu export et;
        /// </summary>
        public async Task<ExportResult> ExportToGodotAsync(GodotExportRequest request)
        {
            try
            {
                _logger.LogInformation($"Exporting animation {request.AnimationId} to Godot format");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Godot formatına dönüştür;
                var godotData = await ConvertToGodotFormatAsync(animation, request);

                // JSON veya binary formatında export et;
                byte[] exportData;
                if (request.ExportAsJson)
                {
                    exportData = await CreateGodotJsonFileAsync(godotData, request);
                }
                else;
                {
                    exportData = await CreateGodotBinaryFileAsync(godotData, request);
                }

                // Dosyayı kaydet;
                var filePath = await SaveExportFileAsync(exportData, request.OutputPath, "godot");

                var result = new ExportResult;
                {
                    Success = true,
                    FilePath = filePath,
                    FileSize = exportData.Length,
                    ExportFormat = request.ExportAsJson ? "JSON" : "Binary"
                };

                _logger.LogInformation($"Animation exported to Godot successfully: {filePath}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error exporting animation to Godot: {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.GodotExportFailed, "Failed to export animation to Godot", ex);
            }
        }

        /// <summary>
        /// NEDA animasyon formatını export et;
        /// </summary>
        public async Task<ExportResult> ExportToNedaFormatAsync(NedaExportRequest request)
        {
            try
            {
                _logger.LogInformation($"Exporting animation {request.AnimationId} to NEDA format");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // NEDA formatına optimize et;
                var nedaData = await ConvertToNedaFormatAsync(animation, request);

                // Binary formatında export et;
                var exportData = await CreateNedaBinaryFileAsync(nedaData, request);

                // Dosyayı kaydet;
                var filePath = await SaveExportFileAsync(exportData, request.OutputPath, "neda");

                var result = new ExportResult;
                {
                    Success = true,
                    FilePath = filePath,
                    FileSize = exportData.Length,
                    ExportFormat = "NEDA Binary",
                    CompressionRatio = CalculateCompressionRatio(animation, nedaData)
                };

                _logger.LogInformation($"Animation exported to NEDA format successfully: {filePath}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error exporting animation to NEDA format: {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.NedaExportFailed, "Failed to export animation to NEDA format", ex);
            }
        }

        /// <summary>
        /// Retarget animasyonu farklı skeleton'a;
        /// </summary>
        public async Task<RetargetResult> RetargetAnimationAsync(RetargetRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("RetargetAnimation");

            try
            {
                _logger.LogInformation($"Retargeting animation {request.SourceAnimationId} to skeleton {request.TargetSkeletonId}");

                // Kaynak animasyonu ve skeleton'ları yükle;
                var sourceAnimation = await _animationRepository.GetByIdAsync(request.SourceAnimationId);
                var sourceSkeleton = await _skeletonManager.GetSkeletonAsync(sourceAnimation.SkeletonId);
                var targetSkeleton = await _skeletonManager.GetSkeletonAsync(request.TargetSkeletonId);

                if (sourceAnimation == null || sourceSkeleton == null || targetSkeleton == null)
                {
                    throw new NEDAException(ErrorCode.RetargetingFailed, "Invalid source animation or skeleton");
                }

                // Retargeting uygula;
                var retargetedData = await _retargetingEngine.RetargetAsync(
                    sourceAnimation,
                    sourceSkeleton,
                    targetSkeleton,
                    request.Options);

                // Retargeted animasyonu kaydet;
                var retargetedAnimation = await SaveRetargetedAnimationAsync(retargetedData, request);

                var result = new RetargetResult;
                {
                    Success = true,
                    RetargetedAnimationId = retargetedAnimation.Id,
                    SourceSkeletonId = sourceAnimation.SkeletonId,
                    TargetSkeletonId = request.TargetSkeletonId,
                    RetargetingMetrics = new RetargetingMetrics;
                    {
                        BoneMappingAccuracy = CalculateBoneMappingAccuracy(retargetedData),
                        PositionError = CalculatePositionError(sourceAnimation, retargetedAnimation),
                        RotationError = CalculateRotationError(sourceAnimation, retargetedAnimation),
                        ProcessingTime = stopwatch.StopAndGetMetrics().ExecutionTime;
                    }
                };

                _logger.LogInformation($"Animation retargeted successfully. New animation ID: {retargetedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error retargeting animation {request.SourceAnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, request);
                throw new NEDAException(ErrorCode.RetargetingFailed, "Failed to retarget animation", ex);
            }
        }

        /// <summary>
        /// Animasyon optimizasyonu uygula;
        /// </summary>
        public async Task<OptimizationResult> OptimizeAnimationAsync(AnimationOptimizationRequest request)
        {
            var stopwatch = _performanceMonitor.StartOperation("OptimizeAnimation");

            try
            {
                _logger.LogInformation($"Optimizing animation {request.AnimationId} with level {request.OptimizationLevel}");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Önceki durumu kaydet;
                var originalMetrics = CalculateAnimationMetrics(animation);

                // Optimizasyon uygula;
                var optimizedData = await _optimizationEngine.OptimizeAsync(animation, request);

                // Optimize edilmiş animasyonu kaydet;
                var optimizedAnimation = await SaveOptimizedAnimationAsync(optimizedData, request);

                // Optimizasyon metriklerini hesapla;
                var optimizedMetrics = CalculateAnimationMetrics(optimizedAnimation);

                var result = new OptimizationResult;
                {
                    Success = true,
                    OptimizedAnimationId = optimizedAnimation.Id,
                    OriginalMetrics = originalMetrics,
                    OptimizedMetrics = optimizedMetrics,
                    OptimizationGain = new OptimizationGain;
                    {
                        SizeReduction = CalculateSizeReduction(originalMetrics, optimizedMetrics),
                        PerformanceImprovement = CalculatePerformanceImprovement(originalMetrics, optimizedMetrics),
                        ProcessingTime = stopwatch.StopAndGetMetrics().ExecutionTime;
                    }
                };

                _logger.LogInformation($"Animation optimized successfully. Size reduction: {result.OptimizationGain.SizeReduction}%");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error optimizing animation {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationOptimizationFailed, "Failed to optimize animation", ex);
            }
        }

        /// <summary>
        /// Frame rate conversion uygula;
        /// </summary>
        public async Task<FrameRateResult> ConvertFrameRateAsync(FrameRateConversionRequest request)
        {
            try
            {
                _logger.LogInformation($"Converting frame rate of animation {request.AnimationId} from {request.SourceFrameRate} to {request.TargetFrameRate}");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Frame rate conversion uygula;
                var convertedData = await ApplyFrameRateConversionAsync(animation, request);

                // Converted animasyonu kaydet;
                var convertedAnimation = await SaveFrameRateConvertedAnimationAsync(convertedData, request);

                var result = new FrameRateResult;
                {
                    Success = true,
                    ConvertedAnimationId = convertedAnimation.Id,
                    SourceFrameRate = request.SourceFrameRate,
                    TargetFrameRate = request.TargetFrameRate,
                    OriginalFrameCount = animation.Frames.Count,
                    ConvertedFrameCount = convertedData.Frames.Count,
                    FrameInterpolationMethod = request.InterpolationMethod;
                };

                _logger.LogInformation($"Frame rate conversion completed. New frame count: {result.ConvertedFrameCount}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error converting frame rate of animation {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.FrameRateConversionFailed, "Failed to convert frame rate", ex);
            }
        }

        /// <summary>
        /// Animasyon compression uygula;
        /// </summary>
        public async Task<CompressionResult> CompressAnimationAsync(CompressionRequest request)
        {
            try
            {
                _logger.LogInformation($"Compressing animation {request.AnimationId} with method {request.CompressionMethod}");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Original boyutu hesapla;
                var originalSize = CalculateAnimationSize(animation);

                // Compression uygula;
                var compressedData = await ApplyCompressionMethodAsync(animation, request);

                // Compressed animasyonu kaydet;
                var compressedAnimation = await SaveCompressedAnimationAsync(compressedData, request);

                // Compressed boyutu hesapla;
                var compressedSize = CalculateAnimationSize(compressedAnimation);

                var result = new CompressionResult;
                {
                    Success = true,
                    CompressedAnimationId = compressedAnimation.Id,
                    OriginalSize = originalSize,
                    CompressedSize = compressedSize,
                    CompressionRatio = (float)originalSize / compressedSize,
                    CompressionMethod = request.CompressionMethod,
                    QualityLoss = CalculateQualityLoss(animation, compressedAnimation)
                };

                _logger.LogInformation($"Animation compressed successfully. Compression ratio: {result.CompressionRatio}x");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error compressing animation {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationCompressionFailed, "Failed to compress animation", ex);
            }
        }

        /// <summary>
        /// Animation baking uygula;
        /// </summary>
        public async Task<BakingResult> BakeAnimationAsync(BakingRequest request)
        {
            try
            {
                _logger.LogInformation($"Baking animation {request.AnimationId}");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Baking uygula;
                var bakedData = await ApplyBakingAsync(animation, request);

                // Baked animasyonu kaydet;
                var bakedAnimation = await SaveBakedAnimationAsync(bakedData, request);

                var result = new BakingResult;
                {
                    Success = true,
                    BakedAnimationId = bakedAnimation.Id,
                    BakingMethod = request.BakingMethod,
                    BakeToWorldSpace = request.BakeToWorldSpace,
                    RemoveInverseKinematics = request.RemoveInverseKinematics,
                    FrameCount = bakedData.Frames.Count;
                };

                _logger.LogInformation($"Animation baked successfully. Baked animation ID: {bakedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error baking animation {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationBakingFailed, "Failed to bake animation", ex);
            }
        }

        /// <summary>
        /// Animation mirroring uygula;
        /// </summary>
        public async Task<MirroringResult> MirrorAnimationAsync(MirroringRequest request)
        {
            try
            {
                _logger.LogInformation($"Mirroring animation {request.AnimationId} across {request.MirrorAxis} axis");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Mirroring uygula;
                var mirroredData = await ApplyMirroringAsync(animation, request);

                // Mirrored animasyonu kaydet;
                var mirroredAnimation = await SaveMirroredAnimationAsync(mirroredData, request);

                var result = new MirroringResult;
                {
                    Success = true,
                    MirroredAnimationId = mirroredAnimation.Id,
                    MirrorAxis = request.MirrorAxis,
                    BoneMapping = request.BoneMapping,
                    PreserveOriginal = request.PreserveOriginal;
                };

                _logger.LogInformation($"Animation mirrored successfully. Mirrored animation ID: {mirroredAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error mirroring animation {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationMirroringFailed, "Failed to mirror animation", ex);
            }
        }

        /// <summary>
        /// Animation looping uygula;
        /// </summary>
        public async Task<LoopingResult> MakeAnimationLoopAsync(LoopingRequest request)
        {
            try
            {
                _logger.LogInformation($"Making animation {request.AnimationId} loop");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Loop detection;
                var loopPoints = await DetectLoopPointsAsync(animation, request);

                // Loop uygula;
                var loopedData = await ApplyLoopingAsync(animation, loopPoints, request);

                // Looped animasyonu kaydet;
                var loopedAnimation = await SaveLoopedAnimationAsync(loopedData, request);

                var result = new LoopingResult;
                {
                    Success = true,
                    LoopedAnimationId = loopedAnimation.Id,
                    LoopStartFrame = loopPoints.StartFrame,
                    LoopEndFrame = loopPoints.EndFrame,
                    SmoothTransition = request.SmoothTransition,
                    TransitionFrames = request.TransitionFrames,
                    IsSeamlessLoop = CheckSeamlessLoop(loopedData)
                };

                _logger.LogInformation($"Animation loop created successfully. Loop frames: {loopPoints.StartFrame}-{loopPoints.EndFrame}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error making animation loop {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationLoopingFailed, "Failed to make animation loop", ex);
            }
        }

        /// <summary>
        /// Animation splitting uygula;
        /// </summary>
        public async Task<SplittingResult> SplitAnimationAsync(SplittingRequest request)
        {
            try
            {
                _logger.LogInformation($"Splitting animation {request.AnimationId} into {request.Segments.Count} segments");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                var splitAnimations = new List<AnimationData>();

                // Her segment için split uygula;
                foreach (var segment in request.Segments)
                {
                    var segmentData = await ExtractAnimationSegmentAsync(animation, segment);
                    var savedSegment = await SaveAnimationSegmentAsync(segmentData, segment, request);
                    splitAnimations.Add(savedSegment);
                }

                var result = new SplittingResult;
                {
                    Success = true,
                    OriginalAnimationId = request.AnimationId,
                    SplitAnimations = splitAnimations.Select(a => a.Id).ToList(),
                    SegmentCount = request.Segments.Count,
                    PreserveOriginal = request.PreserveOriginal;
                };

                _logger.LogInformation($"Animation split successfully into {splitAnimations.Count} segments");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error splitting animation {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationSplittingFailed, "Failed to split animation", ex);
            }
        }

        /// <summary>
        /// Animation merging uygula;
        /// </summary>
        public async Task<MergingResult> MergeAnimationsAsync(MergingRequest request)
        {
            try
            {
                _logger.LogInformation($"Merging {request.AnimationIds.Count} animations");

                // Tüm animasyonları yükle;
                var animations = new List<AnimationData>();
                foreach (var animationId in request.AnimationIds)
                {
                    var animation = await _animationRepository.GetByIdAsync(animationId);
                    if (animation == null)
                    {
                        throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {animationId} not found");
                    }
                    animations.Add(animation);
                }

                // Merge uygula;
                var mergedData = await ApplyMergingAsync(animations, request);

                // Merged animasyonu kaydet;
                var mergedAnimation = await SaveMergedAnimationAsync(mergedData, request);

                var result = new MergingResult;
                {
                    Success = true,
                    MergedAnimationId = mergedAnimation.Id,
                    SourceAnimationIds = request.AnimationIds,
                    MergeMethod = request.MergeMethod,
                    TransitionFrames = request.TransitionFrames,
                    TotalDuration = mergedData.Duration;
                };

                _logger.LogInformation($"Animations merged successfully. Merged animation ID: {mergedAnimation.Id}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error merging animations");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.AnimationMergingFailed, "Failed to merge animations", ex);
            }
        }

        /// <summary>
        /// Animation preview oluştur;
        /// </summary>
        public async Task<PreviewResult> CreateAnimationPreviewAsync(PreviewRequest request)
        {
            try
            {
                _logger.LogInformation($"Creating preview for animation {request.AnimationId}");

                // Animasyonu yükle;
                var animation = await _animationRepository.GetByIdAsync(request.AnimationId);
                if (animation == null)
                {
                    throw new NEDAException(ErrorCode.AnimationNotFound, $"Animation {request.AnimationId} not found");
                }

                // Preview frames oluştur;
                var previewFrames = await GeneratePreviewFramesAsync(animation, request);

                // Video veya GIF oluştur;
                byte[] previewData;
                if (request.OutputFormat == PreviewFormat.Video)
                {
                    previewData = await CreateVideoPreviewAsync(previewFrames, request);
                }
                else;
                {
                    previewData = await CreateGifPreviewAsync(previewFrames, request);
                }

                // Preview'ı kaydet;
                var previewPath = await SavePreviewFileAsync(previewData, request);

                var result = new PreviewResult;
                {
                    Success = true,
                    PreviewPath = previewPath,
                    PreviewFormat = request.OutputFormat,
                    FrameCount = previewFrames.Count,
                    Resolution = request.Resolution,
                    Duration = request.Duration;
                };

                _logger.LogInformation($"Animation preview created successfully: {previewPath}");
                return result;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating animation preview for {request.AnimationId}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Medium, request);
                throw new NEDAException(ErrorCode.PreviewCreationFailed, "Failed to create animation preview", ex);
            }
        }

        /// <summary>
        /// Conversion pipeline oluştur;
        /// </summary>
        public async Task<ConversionPipeline> CreateConversionPipelineAsync(PipelineDefinition definition)
        {
            try
            {
                _logger.LogInformation($"Creating conversion pipeline: {definition.Name}");

                ValidatePipelineDefinition(definition);

                var pipeline = new ConversionPipeline;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = definition.Name,
                    Description = definition.Description,
                    Steps = new List<PipelineStep>(),
                    InputFormat = definition.InputFormat,
                    OutputFormat = definition.OutputFormat,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                };

                // Pipeline step'lerini oluştur;
                foreach (var stepDef in definition.Steps)
                {
                    var step = await CreatePipelineStepAsync(stepDef);
                    pipeline.Steps.Add(step);
                }

                // Pipeline'ı kaydet;
                await SaveConversionPipelineAsync(pipeline);

                _logger.LogInformation($"Conversion pipeline created: {pipeline.Id}");
                return pipeline;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error creating conversion pipeline: {definition.Name}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.High, definition);
                throw new NEDAException(ErrorCode.PipelineCreationFailed, "Failed to create conversion pipeline", ex);
            }
        }

        /// <summary>
        /// Supported formatları getir;
        /// </summary>
        public async Task<List<SupportedFormat>> GetSupportedFormatsAsync()
        {
            try
            {
                var cacheKey = "supported_formats";
                var cached = await _cacheManager.GetAsync<List<SupportedFormat>>(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                var formats = new List<SupportedFormat>
                {
                    new SupportedFormat;
                    {
                        Format = "FBX",
                        Extension = ".fbx",
                        Type = FormatType.Binary,
                        Category = FormatCategory.IndustryStandard,
                        SupportedOperations = new List<FormatOperation>
                        {
                            FormatOperation.Import,
                            FormatOperation.Export,
                            FormatOperation.Convert;
                        },
                        Features = new List<string> { "Animation", "Mesh", "Materials", "Skeleton" }
                    },
                    new SupportedFormat;
                    {
                        Format = "GLTF",
                        Extension = ".gltf,.glb",
                        Type = FormatType.BinaryAndText,
                        Category = FormatCategory.WebStandard,
                        SupportedOperations = new List<FormatOperation>
                        {
                            FormatOperation.Import,
                            FormatOperation.Export;
                        },
                        Features = new List<string> { "Animation", "Mesh", "PBR Materials", "Compression" }
                    },
                    new SupportedFormat;
                    {
                        Format = "BVH",
                        Extension = ".bvh",
                        Type = FormatType.Text,
                        Category = FormatCategory.MotionCapture,
                        SupportedOperations = new List<FormatOperation>
                        {
                            FormatOperation.Import,
                            FormatOperation.Convert;
                        },
                        Features = new List<string> { "Motion Capture", "Hierarchical Data", "Rotations" }
                    },
                    new SupportedFormat;
                    {
                        Format = "NEDA",
                        Extension = ".neda",
                        Type = FormatType.Binary,
                        Category = FormatCategory.Proprietary,
                        SupportedOperations = new List<FormatOperation>
                        {
                            FormatOperation.Export,
                            FormatOperation.Optimize;
                        },
                        Features = new List<string> { "Compressed", "Optimized", "Runtime Ready" }
                    },
                    new SupportedFormat;
                    {
                        Format = "Unity",
                        Extension = ".anim,.fbx",
                        Type = FormatType.Binary,
                        Category = FormatCategory.GameEngine,
                        SupportedOperations = new List<FormatOperation>
                        {
                            FormatOperation.Export;
                        },
                        Features = new List<string> { "Game Ready", "Compressed", "Humanoid" }
                    },
                    new SupportedFormat;
                    {
                        Format = "Unreal",
                        Extension = ".fbx",
                        Type = FormatType.Binary,
                        Category = FormatCategory.GameEngine,
                        SupportedOperations = new List<FormatOperation>
                        {
                            FormatOperation.Export;
                        },
                        Features = new List<string> { "Game Ready", "Sequencer", "Blueprint" }
                    }
                };

                await _cacheManager.SetAsync(cacheKey, formats, TimeSpan.FromHours(24));
                return formats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting supported formats");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low);
                throw new NEDAException(ErrorCode.FormatRetrievalFailed, "Failed to retrieve supported formats", ex);
            }
        }

        /// <summary>
        /// Format detaylarını getir;
        /// </summary>
        public async Task<FormatDetails> GetFormatDetailsAsync(string format)
        {
            try
            {
                var cacheKey = $"format_details_{format.ToLower()}";
                var cached = await _cacheManager.GetAsync<FormatDetails>(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                var details = format.ToUpper() switch;
                {
                    "FBX" => CreateFbxFormatDetails(),
                    "GLTF" => CreateGltfFormatDetails(),
                    "BVH" => CreateBvhFormatDetails(),
                    "NEDA" => CreateNedaFormatDetails(),
                    "UNITY" => CreateUnityFormatDetails(),
                    "UNREAL" => CreateUnrealFormatDetails(),
                    _ => throw new NEDAException(ErrorCode.FormatNotFound, $"Format {format} not found")
                };

                await _cacheManager.SetAsync(cacheKey, details, TimeSpan.FromHours(12));
                return details;
            }
            catch (NEDAException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting format details for {format}");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low, new { format });
                throw new NEDAException(ErrorCode.FormatDetailsRetrievalFailed, "Failed to retrieve format details", ex);
            }
        }

        /// <summary>
        /// Conversion istatistiklerini getir;
        /// </summary>
        public async Task<ConversionStatistics> GetConversionStatisticsAsync()
        {
            try
            {
                var cacheKey = "conversion_statistics";
                var cached = await _cacheManager.GetAsync<ConversionStatistics>(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                // İstatistikleri hesapla;
                var stats = new ConversionStatistics;
                {
                    TotalConversions = await GetTotalConversionsAsync(),
                    SuccessfulConversions = await GetSuccessfulConversionsAsync(),
                    FailedConversions = await GetFailedConversionsAsync(),
                    AverageConversionTime = await GetAverageConversionTimeAsync(),
                    MostConvertedFormat = await GetMostConvertedFormatAsync(),
                    ConversionSuccessRate = await CalculateSuccessRateAsync(),
                    LastUpdated = DateTime.UtcNow;
                };

                await _cacheManager.SetAsync(cacheKey, stats, TimeSpan.FromMinutes(30));
                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting conversion statistics");
                await _errorReporter.ReportErrorAsync(ex, ErrorSeverity.Low);
                throw new NEDAException(ErrorCode.StatisticsRetrievalFailed, "Failed to retrieve conversion statistics", ex);
            }
        }

        /// <summary>
        /// Cache'i temizle;
        /// </summary>
        public async Task ClearConversionCacheAsync()
        {
            try
            {
                await _cacheManager.RemoveByPatternAsync("conversion_*");
                await _cacheManager.RemoveByPatternAsync("format_*");
                await _cacheManager.RemoveByPatternAsync("pipeline_*");

                _logger.LogInformation("Conversion cache cleared");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error clearing conversion cache");
                // Cache temizleme hatası kritik değil;
            }
        }

        #region Private Methods;

        private Dictionary<string, IFormatConverter> InitializeFormatConverters()
        {
            var converters = new Dictionary<string, IFormatConverter>
            {
                { "FBX_TO_GLTF", new FbxToGltfConverter() },
                { "GLTF_TO_FBX", new GltfToFbxConverter() },
                { "BVH_TO_FBX", new BvhToFbxConverter() },
                { "FBX_TO_UNITY", new FbxToUnityConverter() },
                { "FBX_TO_UNREAL", new FbxToUnrealConverter() },
                { "ANY_TO_NEDA", new AnyToNedaConverter() }
            };

            return converters;
        }

        private IFormatConverter GetFormatConverter(string sourceFormat, string targetFormat)
        {
            var key = $"{sourceFormat.ToUpper()}_TO_{targetFormat.ToUpper()}";

            if (_formatConverters.TryGetValue(key, out var converter))
            {
                return converter;
            }

            // Fallback: generic converter;
            return _formatConverters.GetValueOrDefault("ANY_TO_NEDA");
        }

        private void ValidateConversionRequest(ConversionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrEmpty(request.SourceFormat))
                throw new NEDAException(ErrorCode.InvalidConversionRequest, "Source format cannot be empty");

            if (string.IsNullOrEmpty(request.TargetFormat))
                throw new NEDAException(ErrorCode.InvalidConversionRequest, "Target format cannot be empty");

            if (string.IsNullOrEmpty(request.SourceData))
                throw new NEDAException(ErrorCode.InvalidConversionRequest, "Source data cannot be empty");
        }

        private async Task<SourceAnimationData> LoadSourceAnimationAsync(ConversionRequest request)
        {
            // Source data'yı parse et;
            var parser = GetParserForFormat(request.SourceFormat);
            if (parser == null)
            {
                throw new NEDAException(ErrorCode.ParserNotFound,
                    $"No parser found for format: {request.SourceFormat}");
            }

            return await parser.ParseAsync(request.SourceData, request.Options);
        }

        private IParser GetParserForFormat(string format)
        {
            return format.ToUpper() switch;
            {
                "FBX" => new FbxParser(),
                "GLTF" => new GltfParser(),
                "BVH" => new BvhParser(),
                "COLLADA" => new ColladaParser(),
                "NEDA" => new NedaParser(),
                _ => null;
            };
        }

        private async Task<ConvertedAnimationData> ApplyOptimizationAsync(
            ConvertedAnimationData data,
            OptimizationLevel level)
        {
            var optimizer = new AnimationOptimizer();
            return await optimizer.OptimizeAsync(data, level);
        }

        private async Task<ConvertedAnimationData> ApplyCompressionAsync(
            ConvertedAnimationData data,
            CompressionLevel level)
        {
            var compressor = new AnimationCompressor();
            return await compressor.CompressAsync(data, level);
        }

        private async Task<ConversionResult> SaveConvertedAnimationAsync(
            ConvertedAnimationData convertedData,
            ConversionRequest request)
        {
            // Animasyonu veritabanına kaydet;
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.OutputName ?? $"Converted_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Format = request.TargetFormat,
                Data = convertedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceFormat", request.SourceFormat },
                    { "TargetFormat", request.TargetFormat },
                    { "ConversionTime", DateTime.UtcNow },
                    { "Options", request.Options }
                },
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow;
            };

            var savedAnimation = await _animationRepository.AddAsync(animation);

            return new ConversionResult;
            {
                Success = true,
                ConvertedAnimationId = savedAnimation.Id,
                SourceFormat = request.SourceFormat,
                TargetFormat = request.TargetFormat,
                ConversionTime = DateTime.UtcNow,
                FileSize = convertedData.BinaryData.Length,
                FromCache = false;
            };
        }

        private string CreateConversionCacheKey(ConversionRequest request)
        {
            // Request'ten cache key oluştur;
            var keyData = $"{request.SourceFormat}_{request.TargetFormat}_{request.SourceData.GetHashCode()}";
            if (request.Options != null)
            {
                keyData += $"_{request.Options.GetHashCode()}";
            }

            return $"conversion_{keyData.GetHashCode():X}";
        }

        private async Task CacheConversionResultAsync(string cacheKey, ConversionResult result)
        {
            await _cacheManager.SetAsync(cacheKey, result, TimeSpan.FromHours(_appConfig.AnimationConverter.CacheDurationHours));
        }

        private async Task UpdateConversionStatisticsAsync(string sourceFormat, string targetFormat, bool success)
        {
            try
            {
                // İstatistikleri güncelle;
                var statsKey = "conversion_stats";
                var stats = await _cacheManager.GetAsync<ConversionStats>(statsKey) ?? new ConversionStats();

                stats.TotalConversions++;
                if (success) stats.SuccessfulConversions++;
                else stats.FailedConversions++;

                // Format pair istatistikleri;
                var formatPair = $"{sourceFormat}_{targetFormat}";
                if (!stats.FormatPairCounts.ContainsKey(formatPair))
                {
                    stats.FormatPairCounts[formatPair] = 0;
                }
                stats.FormatPairCounts[formatPair]++;

                await _cacheManager.SetAsync(statsKey, stats, TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update conversion statistics");
            }
        }

        private async Task ProcessBatchInParallelAsync(
            BatchConversionRequest request,
            List<ConversionResult> results,
            List<FailedConversion> failedConversions)
        {
            var semaphore = new SemaphoreSlim(_appConfig.AnimationConverter.MaxParallelConversions);
            var tasks = new List<Task>();

            foreach (var conversionRequest in request.Conversions)
            {
                await semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var result = await ConvertAnimationAsync(conversionRequest);
                        lock (results)
                        {
                            results.Add(result);
                        }
                    }
                    catch (Exception ex)
                    {
                        var failed = new FailedConversion;
                        {
                            Request = conversionRequest,
                            ErrorMessage = ex.Message,
                            ErrorCode = ex is NEDAException ne ? ne.ErrorCode : ErrorCode.UnknownError;
                        };

                        lock (failedConversions)
                        {
                            failedConversions.Add(failed);
                        }

                        _logger.LogWarning(ex, $"Batch conversion failed for request");
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);
        }

        private async Task ProcessBatchInSequenceAsync(
            BatchConversionRequest request,
            List<ConversionResult> results,
            List<FailedConversion> failedConversions)
        {
            foreach (var conversionRequest in request.Conversions)
            {
                try
                {
                    var result = await ConvertAnimationAsync(conversionRequest);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    var failed = new FailedConversion;
                    {
                        Request = conversionRequest,
                        ErrorMessage = ex.Message,
                        ErrorCode = ex is NEDAException ne ? ne.ErrorCode : ErrorCode.UnknownError;
                    };

                    failedConversions.Add(failed);
                    _logger.LogWarning(ex, $"Batch conversion failed for request");
                }
            }
        }

        private void ValidateFileExists(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new NEDAException(ErrorCode.FileNotFound, $"File not found: {filePath}");
            }
        }

        private async Task<AnimationData> ExtractFbxAnimationDataAsync(FbxData fbxData, FbxImportRequest request)
        {
            var extractor = new FbxAnimationExtractor();
            return await extractor.ExtractAsync(fbxData, request);
        }

        private async Task<AnimationData> ApplySkeletonMappingAsync(AnimationData animationData, string targetSkeletonId)
        {
            var mapper = new SkeletonMapper();
            return await mapper.MapAsync(animationData, targetSkeletonId);
        }

        private async Task<AnimationData> OptimizeImportedAnimationAsync(AnimationData animationData, OptimizationLevel level)
        {
            var optimizer = new ImportOptimizer();
            return await optimizer.OptimizeAsync(animationData, level);
        }

        private async Task<AnimationData> SaveImportedAnimationAsync(AnimationData animationData, ImportRequest request)
        {
            animationData.Id = Guid.NewGuid().ToString();
            animationData.SourceFormat = request.GetType().Name.Replace("ImportRequest", "");
            animationData.ImportTime = DateTime.UtcNow;

            return await _animationRepository.AddAsync(animationData);
        }

        private async Task<AnimationData> ExtractGltfAnimationDataAsync(GltfData gltfData, GltfImportRequest request)
        {
            var extractor = new GltfAnimationExtractor();
            return await extractor.ExtractAsync(gltfData, request);
        }

        private async Task<AnimationData> ProcessGltfBinaryDataAsync(AnimationData animationData, GltfData gltfData)
        {
            var processor = new GltfBinaryProcessor();
            return await processor.ProcessAsync(animationData, gltfData);
        }

        private async Task<AnimationData> ExtractColladaAnimationDataAsync(ColladaData colladaData, ColladaImportRequest request)
        {
            var extractor = new ColladaAnimationExtractor();
            return await extractor.ExtractAsync(colladaData, request);
        }

        private async Task<AnimationData> ProcessColladaHierarchyAsync(AnimationData animationData, ColladaData colladaData)
        {
            var processor = new ColladaHierarchyProcessor();
            return await processor.ProcessAsync(animationData, colladaData);
        }

        private async Task<AnimationData> ProcessBvhHierarchyAsync(BvhData bvhData, BvhImportRequest request)
        {
            var processor = new BvhHierarchyProcessor();
            return await processor.ProcessAsync(bvhData, request);
        }

        private async Task<AnimationData> FixBvhRotationOrderAsync(AnimationData animationData)
        {
            var fixer = new BvhRotationFixer();
            return await fixer.FixAsync(animationData);
        }

        private async Task<AnimationData> ApplyScaleFactorAsync(AnimationData animationData, float scaleFactor)
        {
            var scaler = new AnimationScaler();
            return await scaler.ScaleAsync(animationData, scaleFactor);
        }

        private async Task<AnimationData> ProcessMixamoAnimationAsync(MixamoImportRequest request)
        {
            var processor = new MixamoProcessor();
            return await processor.ProcessAsync(request);
        }

        private async Task<AnimationData> AlignToTPoseAsync(AnimationData animationData)
        {
            var aligner = new TPoseAligner();
            return await aligner.AlignAsync(animationData);
        }

        private async Task<AnimationData> ExtractRootMotionAsync(AnimationData animationData)
        {
            var extractor = new RootMotionExtractor();
            return await extractor.ExtractAsync(animationData);
        }

        private MixamoCompatibility CheckMixamoCompatibility(AnimationData animationData)
        {
            var checker = new MixamoCompatibilityChecker();
            return checker.Check(animationData);
        }

        private async Task<UnityAnimationData> ConvertToUnityFormatAsync(AnimationData animation, UnityExportRequest request)
        {
            var converter = new UnityConverter();
            return await converter.ConvertAsync(animation, request);
        }

        private async Task<byte[]> CreateUnityFbxFileAsync(UnityAnimationData unityData, UnityExportRequest request)
        {
            var creator = new UnityFbxCreator();
            return await creator.CreateAsync(unityData, request);
        }

        private async Task<byte[]> CreateUnityAnimFileAsync(UnityAnimationData unityData, UnityExportRequest request)
        {
            var creator = new UnityAnimCreator();
            return await creator.CreateAsync(unityData, request);
        }

        private async Task<string> SaveExportFileAsync(byte[] data, string outputPath, string format)
        {
            if (string.IsNullOrEmpty(outputPath))
            {
                outputPath = Path.Combine(
                    _appConfig.AnimationConverter.ExportDirectory,
                    $"{Guid.NewGuid()}.{format.ToLower()}");
            }

            await _fileService.WriteAllBytesAsync(outputPath, data);
            return outputPath;
        }

        private async Task<UnrealAnimationData> ConvertToUnrealFormatAsync(AnimationData animation, UnrealExportRequest request)
        {
            var converter = new UnrealConverter();
            return await converter.ConvertAsync(animation, request);
        }

        private async Task<byte[]> CreateUnrealFbxFileAsync(UnrealAnimationData unrealData, UnrealExportRequest request)
        {
            var creator = new UnrealFbxCreator();
            return await creator.CreateAsync(unrealData, request);
        }

        private UnrealCompatibility CheckUnrealCompatibility(UnrealAnimationData unrealData)
        {
            var checker = new UnrealCompatibilityChecker();
            return checker.Check(unrealData);
        }

        private async Task<GodotAnimationData> ConvertToGodotFormatAsync(AnimationData animation, GodotExportRequest request)
        {
            var converter = new GodotConverter();
            return await converter.ConvertAsync(animation, request);
        }

        private async Task<byte[]> CreateGodotJsonFileAsync(GodotAnimationData godotData, GodotExportRequest request)
        {
            var creator = new GodotJsonCreator();
            return await creator.CreateAsync(godotData, request);
        }

        private async Task<byte[]> CreateGodotBinaryFileAsync(GodotAnimationData godotData, GodotExportRequest request)
        {
            var creator = new GodotBinaryCreator();
            return await creator.CreateAsync(godotData, request);
        }

        private async Task<NedaAnimationData> ConvertToNedaFormatAsync(AnimationData animation, NedaExportRequest request)
        {
            var converter = new NedaConverter();
            return await converter.ConvertAsync(animation, request);
        }

        private async Task<byte[]> CreateNedaBinaryFileAsync(NedaAnimationData nedaData, NedaExportRequest request)
        {
            var creator = new NedaBinaryCreator();
            return await creator.CreateAsync(nedaData, request);
        }

        private float CalculateCompressionRatio(AnimationData original, NedaAnimationData compressed)
        {
            var originalSize = CalculateAnimationSize(original);
            var compressedSize = compressed.BinaryData.Length;
            return (float)originalSize / compressedSize;
        }

        private async Task<AnimationData> SaveRetargetedAnimationAsync(RetargetedData retargetedData, RetargetRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "Retargeted"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                SkeletonId = request.TargetSkeletonId,
                Data = retargetedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.SourceAnimationId },
                    { "RetargetingOptions", request.Options },
                    { "RetargetingTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private float CalculateBoneMappingAccuracy(RetargetedData retargetedData)
        {
            // Bone mapping accuracy hesapla;
            return 1.0f; // Placeholder;
        }

        private float CalculatePositionError(AnimationData source, AnimationData target)
        {
            // Position error hesapla;
            return 0.0f; // Placeholder;
        }

        private float CalculateRotationError(AnimationData source, AnimationData target)
        {
            // Rotation error hesapla;
            return 0.0f; // Placeholder;
        }

        private AnimationMetrics CalculateAnimationMetrics(AnimationData animation)
        {
            return new AnimationMetrics;
            {
                FrameCount = animation.Frames.Count,
                BoneCount = animation.BoneCount,
                Duration = animation.Duration,
                MemorySize = CalculateAnimationSize(animation),
                CompressionRatio = animation.CompressionRatio;
            };
        }

        private long CalculateAnimationSize(AnimationData animation)
        {
            // Animasyon boyutunu hesapla;
            return animation.Data?.Length ?? 0;
        }

        private async Task<AnimationData> SaveOptimizedAnimationAsync(OptimizedData optimizedData, AnimationOptimizationRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "Optimized"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = optimizedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "OptimizationLevel", request.OptimizationLevel },
                    { "OptimizationTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private float CalculateSizeReduction(AnimationMetrics original, AnimationMetrics optimized)
        {
            if (original.MemorySize == 0) return 0;
            return (1 - (float)optimized.MemorySize / original.MemorySize) * 100;
        }

        private float CalculatePerformanceImprovement(AnimationMetrics original, AnimationMetrics optimized)
        {
            // Performance improvement hesapla;
            return 0.0f; // Placeholder;
        }

        private async Task<ConvertedAnimationData> ApplyFrameRateConversionAsync(
            AnimationData animation,
            FrameRateConversionRequest request)
        {
            var converter = new FrameRateConverter();
            return await converter.ConvertAsync(animation, request);
        }

        private async Task<AnimationData> SaveFrameRateConvertedAnimationAsync(
            ConvertedAnimationData convertedData,
            FrameRateConversionRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "FrameRateConverted"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = convertedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "SourceFrameRate", request.SourceFrameRate },
                    { "TargetFrameRate", request.TargetFrameRate },
                    { "ConversionTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private async Task<CompressedAnimationData> ApplyCompressionMethodAsync(
            AnimationData animation,
            CompressionRequest request)
        {
            var compressor = new AnimationCompressor();
            return await compressor.CompressAsync(animation, request);
        }

        private async Task<AnimationData> SaveCompressedAnimationAsync(
            CompressedAnimationData compressedData,
            CompressionRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "Compressed"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = compressedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "CompressionMethod", request.CompressionMethod },
                    { "CompressionTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private float CalculateQualityLoss(AnimationData original, AnimationData compressed)
        {
            // Quality loss hesapla;
            return 0.0f; // Placeholder;
        }

        private async Task<BakedAnimationData> ApplyBakingAsync(AnimationData animation, BakingRequest request)
        {
            var baker = new AnimationBaker();
            return await baker.BakeAsync(animation, request);
        }

        private async Task<AnimationData> SaveBakedAnimationAsync(BakedAnimationData bakedData, BakingRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "Baked"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = bakedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "BakingMethod", request.BakingMethod },
                    { "BakingTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private async Task<MirroredAnimationData> ApplyMirroringAsync(AnimationData animation, MirroringRequest request)
        {
            var mirror = new AnimationMirror();
            return await mirror.MirrorAsync(animation, request);
        }

        private async Task<AnimationData> SaveMirroredAnimationAsync(MirroredAnimationData mirroredData, MirroringRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "Mirrored"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = mirroredData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "MirrorAxis", request.MirrorAxis },
                    { "MirroringTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private async Task<LoopPoints> DetectLoopPointsAsync(AnimationData animation, LoopingRequest request)
        {
            var detector = new LoopDetector();
            return await detector.DetectAsync(animation, request);
        }

        private async Task<LoopedAnimationData> ApplyLoopingAsync(
            AnimationData animation,
            LoopPoints loopPoints,
            LoopingRequest request)
        {
            var looper = new AnimationLooper();
            return await looper.LoopAsync(animation, loopPoints, request);
        }

        private async Task<AnimationData> SaveLoopedAnimationAsync(LoopedAnimationData loopedData, LoopingRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{request.OutputName ?? "Looped"}_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = loopedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "LoopPoints", loopedData.LoopPoints },
                    { "LoopingTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private bool CheckSeamlessLoop(LoopedAnimationData loopedData)
        {
            var checker = new SeamlessLoopChecker();
            return checker.Check(loopedData);
        }

        private async Task<AnimationSegmentData> ExtractAnimationSegmentAsync(
            AnimationData animation,
            AnimationSegment segment)
        {
            var extractor = new AnimationSegmentExtractor();
            return await extractor.ExtractAsync(animation, segment);
        }

        private async Task<AnimationData> SaveAnimationSegmentAsync(
            AnimationSegmentData segmentData,
            AnimationSegment segment,
            SplittingRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = segment.Name ?? $"Segment_{segment.StartFrame}_{segment.EndFrame}",
                Data = segmentData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationId", request.AnimationId },
                    { "StartFrame", segment.StartFrame },
                    { "EndFrame", segment.EndFrame },
                    { "SegmentTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private async Task<MergedAnimationData> ApplyMergingAsync(
            List<AnimationData> animations,
            MergingRequest request)
        {
            var merger = new AnimationMerger();
            return await merger.MergeAsync(animations, request);
        }

        private async Task<AnimationData> SaveMergedAnimationAsync(MergedAnimationData mergedData, MergingRequest request)
        {
            var animation = new AnimationData;
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.OutputName ?? $"Merged_{DateTime.UtcNow:yyyyMMdd_HHmmss}",
                Data = mergedData.BinaryData,
                Metadata = new Dictionary<string, object>
                {
                    { "SourceAnimationIds", request.AnimationIds },
                    { "MergeMethod", request.MergeMethod },
                    { "MergingTime", DateTime.UtcNow }
                },
                CreatedAt = DateTime.UtcNow;
            };

            return await _animationRepository.AddAsync(animation);
        }

        private async Task<List<PreviewFrame>> GeneratePreviewFramesAsync(AnimationData animation, PreviewRequest request)
        {
            var generator = new PreviewFrameGenerator();
            return await generator.GenerateAsync(animation, request);
        }

        private async Task<byte[]> CreateVideoPreviewAsync(List<PreviewFrame> frames, PreviewRequest request)
        {
            var creator = new VideoPreviewCreator();
            return await creator.CreateAsync(frames, request);
        }

        private async Task<byte[]> CreateGifPreviewAsync(List<PreviewFrame> frames, PreviewRequest request)
        {
            var creator = new GifPreviewCreator();
            return await creator.CreateAsync(frames, request);
        }

        private async Task<string> SavePreviewFileAsync(byte[] previewData, PreviewRequest request)
        {
            var extension = request.OutputFormat == PreviewFormat.Video ? "mp4" : "gif";
            var fileName = $"{request.OutputName ?? "preview"}_{Guid.NewGuid()}.{extension}";
            var filePath = Path.Combine(_appConfig.AnimationConverter.PreviewDirectory, fileName);

            await _fileService.WriteAllBytesAsync(filePath, previewData);
            return filePath;
        }

        private void ValidatePipelineDefinition(PipelineDefinition definition)
        {
            if (definition == null)
                throw new ArgumentNullException(nameof(definition));

            if (string.IsNullOrEmpty(definition.Name))
                throw new NEDAException(ErrorCode.InvalidPipelineDefinition, "Pipeline name cannot be empty");

            if (definition.Steps == null || definition.Steps.Count == 0)
                throw new NEDAException(ErrorCode.InvalidPipelineDefinition, "Pipeline must have at least one step");
        }

        private async Task<PipelineStep> CreatePipelineStepAsync(PipelineStepDefinition stepDef)
        {
            var step = new PipelineStep;
            {
                Id = Guid.NewGuid().ToString(),
                Name = stepDef.Name,
                Type = stepDef.Type,
                Parameters = stepDef.Parameters,
                Order = stepDef.Order;
            };

            return step;
        }

        private async Task SaveConversionPipelineAsync(ConversionPipeline pipeline)
        {
            // Pipeline'ı kaydet (repository implement edilecek)
            // await _pipelineRepository.AddAsync(pipeline);
        }

        private FormatDetails CreateFbxFormatDetails()
        {
            return new FormatDetails;
            {
                Format = "FBX",
                Description = "Autodesk FBX format - Industry standard 3D animation format",
                Version = "2020.0",
                MimeType = "application/octet-stream",
                SupportedFeatures = new List<string>
                {
                    "Skeletal Animation",
                    "Morph Target Animation",
                    "Camera Animation",
                    "Light Animation",
                    "Material Animation"
                },
                Limitations = new List<string>
                {
                    "Binary format, not human readable",
                    "Complex hierarchy can cause issues",
                    "Version compatibility problems"
                },
                RecommendedUse = "Professional 3D animation workflows, game development",
                SampleFile = "sample.fbx"
            };
        }

        private FormatDetails CreateGltfFormatDetails()
        {
            return new FormatDetails;
            {
                Format = "GLTF",
                Description = "GL Transmission Format - Web standard for 3D content",
                Version = "2.0",
                MimeType = "model/gltf+json, model/gltf-binary",
                SupportedFeatures = new List<string>
                {
                    "PBR Materials",
                    "Skeletal Animation",
                    "Morph Targets",
                    "Compression (Draco)",
                    "WebGL ready"
                },
                Limitations = new List<string>
                {
                    "Limited industry tool support",
                    "Newer format, less established"
                },
                RecommendedUse = "Web applications, mobile apps, real-time rendering",
                SampleFile = "sample.glb"
            };
        }

        private FormatDetails CreateBvhFormatDetails()
        {
            return new FormatDetails;
            {
                Format = "BVH",
                Description = "Biovision Hierarchy - Motion capture data format",
                Version = "1.0",
                MimeType = "text/plain",
                SupportedFeatures = new List<string>
                {
                    "Motion capture data",
                    "Hierarchical bone structure",
                    "Rotation data in Euler angles"
                },
                Limitations = new List<string>
                {
                    "No mesh or material support",
                    "Euler rotations can cause gimbal lock",
                    "Limited to skeletal animation"
                },
                RecommendedUse = "Motion capture data import, character animation",
                SampleFile = "sample.bvh"
            };
        }

        private FormatDetails CreateNedaFormatDetails()
        {
            return new FormatDetails;
            {
                Format = "NEDA",
                Description = "NEDA proprietary animation format - Optimized for runtime performance",
                Version = "1.0",
                MimeType = "application/x-neda-animation",
                SupportedFeatures = new List<string>
                {
                    "Highly compressed",
                    "Runtime optimized",
                    "Platform independent",
                    "Real-time decompression"
                },
                Limitations = new List<string>
                {
                    "Proprietary format",
                    "Requires NEDA runtime",
                    "Limited third-party support"
                },
                RecommendedUse = "NEDA runtime applications, performance-critical scenarios",
                SampleFile = "sample.neda"
            };
        }

        private FormatDetails CreateUnityFormatDetails()
        {
            return new FormatDetails;
            {
                Format = "Unity",
                Description = "Unity Engine animation format",
                Version = "2022.3",
                MimeType = "application/octet-stream",
                SupportedFeatures = new List<string>
                {
                    "Humanoid animation",
                    "Generic animation",
                    "Animation curves",
                    "Events and parameters"
                },
                Limitations = new List<string>
                {
                    "Unity specific",
                    "Requires Unity runtime",
                    "Limited export options"
                },
                RecommendedUse = "Unity game development, Unity projects",
                SampleFile = "sample.anim"
            };
        }

        private FormatDetails CreateUnrealFormatDetails()
        {
            return new FormatDetails;
            {
                Format = "Unreal",
                Description = "Unreal Engine animation format",
                Version = "5.3",
                MimeType = "application/octet-stream",
                SupportedFeatures = new List<string>
                {
                    "Animation sequences",
                    "Blend spaces",
                    "Animation blueprints",
                    "Root motion"
                },
                Limitations = new List<string>
                {
                    "Unreal specific",
                    "Complex import/export process",
                    "Large file sizes"
                },
                RecommendedUse = "Unreal Engine game development, cinematic sequences",
                SampleFile = "sample.fbx"
            };
        }

        private async Task<int> GetTotalConversionsAsync()
        {
            // Veritabanından total conversions sayısını getir;
            return 0; // Placeholder;
        }

        private async Task<int> GetSuccessfulConversionsAsync()
        {
            // Veritabanından successful conversions sayısını getir;
            return 0; // Placeholder;
        }

        private async Task<int> GetFailedConversionsAsync()
        {
            // Veritabanından failed conversions sayısını getir;
            return 0; // Placeholder;
        }

        private async Task<double> GetAverageConversionTimeAsync()
        {
            // Ortalama conversion time hesapla;
            return 0.0; // Placeholder;
        }

        private async Task<string> GetMostConvertedFormatAsync()
        {
            // En çok dönüştürülen formatı getir;
            return "FBX"; // Placeholder;
        }

        private async Task<double> CalculateSuccessRateAsync()
        {
            var total = await GetTotalConversionsAsync();
            var successful = await GetSuccessfulConversionsAsync();

            if (total == 0) return 0.0;
            return (double)successful / total * 100;
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Managed kaynakları serbest bırak;
                    _formatConverters.Clear();
                    ClearConversionCacheAsync().Wait();

                    _logger.LogInformation("AnimationConverter disposed");
                }

                _disposed = true;
            }
        }

        ~AnimationConverter()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Interfaces;

    public interface IFormatConverter;
    {
        Task<ConvertedAnimationData> ConvertAsync(SourceAnimationData source, ConversionOptions options);
    }

    public interface IParser;
    {
        Task<SourceAnimationData> ParseAsync(string sourceData, ConversionOptions options);
    }

    // Request Classes;
    public class ConversionRequest;
    {
        public string SourceFormat { get; set; }
        public string TargetFormat { get; set; }
        public string SourceData { get; set; } // Base64 encoded or file path;
        public string OutputName { get; set; }
        public ConversionOptions Options { get; set; }
    }

    public class ConversionOptions;
    {
        public bool EnableOptimization { get; set; }
        public OptimizationLevel OptimizationLevel { get; set; }
        public bool EnableCompression { get; set; }
        public CompressionLevel CompressionLevel { get; set; }
        public bool PreserveHierarchy { get; set; }
        public float ScaleFactor { get; set; } = 1.0f;
        public bool GenerateMetadata { get; set; } = true;
    }

    public class BatchConversionRequest;
    {
        public List<ConversionRequest> Conversions { get; set; }
        public bool ParallelProcessing { get; set; } = true;
        public string BatchName { get; set; }
    }

    public class FbxImportRequest : ImportRequest;
    {
        public string TargetSkeletonId { get; set; }
        public bool ExtractMaterials { get; set; }
        public bool ExtractTextures { get; set; }
        public FbxImportOptions ImportOptions { get; set; }
    }

    public class GltfImportRequest : ImportRequest;
    {
        public bool ProcessBinaryData { get; set; } = true;
        public bool ExtractAnimationsSeparately { get; set; }
        public GltfImportOptions ImportOptions { get; set; }
    }

    public class ColladaImportRequest : ImportRequest;
    {
        public bool FlattenHierarchy { get; set; }
        public ColladaImportOptions ImportOptions { get; set; }
    }

    public class BvhImportRequest : ImportRequest;
    {
        public float ScaleFactor { get; set; } = 0.01f; // BVH usually in cm;
        public bool ConvertToMeters { get; set; } = true;
        public RotationOrder SourceRotationOrder { get; set; }
        public RotationOrder TargetRotationOrder { get; set; }
    }

    public class MixamoImportRequest : ImportRequest;
    {
        public bool AlignToTPose { get; set; } = true;
        public bool ExtractRootMotion { get; set; }
        public string TargetRigType { get; set; } = "Humanoid";
        public MixamoImportOptions ImportOptions { get; set; }
    }

    public abstract class ImportRequest;
    {
        public string FilePath { get; set; }
        public string OutputName { get; set; }
        public bool EnableOptimization { get; set; } = true;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
    }

    public class UnityExportRequest : ExportRequest;
    {
        public bool ExportAsFbx { get; set; } = true;
        public UnityRigType RigType { get; set; } = UnityRigType.Humanoid;
        public bool GenerateAnimatorController { get; set; }
        public UnityExportOptions ExportOptions { get; set; }
    }

    public class UnrealExportRequest : ExportRequest;
    {
        public UnrealSkeletonType SkeletonType { get; set; } = UnrealSkeletonType.Humanoid;
        public bool ExportAnimationBlueprint { get; set; }
        public bool ExportMetaHumanCompatible { get; set; }
        public UnrealExportOptions ExportOptions { get; set; }
    }

    public class GodotExportRequest : ExportRequest;
    {
        public bool ExportAsJson { get; set; } = true;
        public GodotAnimationType AnimationType { get; set; } = GodotAnimationType.AnimationPlayer;
        public GodotExportOptions ExportOptions { get; set; }
    }

    public class NedaExportRequest : ExportRequest;
    {
        public NedaCompressionType CompressionType { get; set; } = NedaCompressionType.High;
        public bool IncludeMetadata { get; set; } = true;
        public bool OptimizeForRuntime { get; set; } = true;
        public NedaExportOptions ExportOptions { get; set; }
    }

    public abstract class ExportRequest;
    {
        public string AnimationId { get; set; }
        public string OutputPath { get; set; }
        public string OutputName { get; set; }
    }

    public class RetargetRequest;
    {
        public string SourceAnimationId { get; set; }
        public string TargetSkeletonId { get; set; }
        public string OutputName { get; set; }
        public RetargetOptions Options { get; set; }
    }

    public class AnimationOptimizationRequest;
    {
        public string AnimationId { get; set; }
        public OptimizationLevel OptimizationLevel { get; set; }
        public string OutputName { get; set; }
        public OptimizationOptions Options { get; set; }
    }

    public class FrameRateConversionRequest;
    {
        public string AnimationId { get; set; }
        public float SourceFrameRate { get; set; }
        public float TargetFrameRate { get; set; }
        public string OutputName { get; set; }
        public InterpolationMethod InterpolationMethod { get; set; }
    }

    public class CompressionRequest;
    {
        public string AnimationId { get; set; }
        public CompressionMethod CompressionMethod { get; set; }
        public string OutputName { get; set; }
        public CompressionOptions Options { get; set; }
    }

    public class BakingRequest;
    {
        public string AnimationId { get; set; }
        public BakingMethod BakingMethod { get; set; }
        public string OutputName { get; set; }
        public bool BakeToWorldSpace { get; set; }
        public bool RemoveInverseKinematics { get; set; }
        public BakingOptions Options { get; set; }
    }

    public class MirroringRequest;
    {
        public string AnimationId { get; set; }
        public MirrorAxis MirrorAxis { get; set; }
        public string OutputName { get; set; }
        public Dictionary<string, string> BoneMapping { get; set; }
        public bool PreserveOriginal { get; set; }
    }

    public class LoopingRequest;
    {
        public string AnimationId { get; set; }
        public string OutputName { get; set; }
        public bool SmoothTransition { get; set; } = true;
        public int TransitionFrames { get; set; } = 10;
        public LoopDetectionMethod DetectionMethod { get; set; }
    }

    public class SplittingRequest;
    {
        public string AnimationId { get; set; }
        public List<AnimationSegment> Segments { get; set; }
        public string OutputNamePattern { get; set; }
        public bool PreserveOriginal { get; set; } = true;
    }

    public class MergingRequest;
    {
        public List<string> AnimationIds { get; set; }
        public string OutputName { get; set; }
        public MergeMethod MergeMethod { get; set; }
        public int TransitionFrames { get; set; } = 5;
        public bool MaintainTiming { get; set; } = true;
    }

    public class PreviewRequest;
    {
        public string AnimationId { get; set; }
        public PreviewFormat OutputFormat { get; set; }
        public string OutputPath { get; set; }
        public string OutputName { get; set; }
        public PreviewResolution Resolution { get; set; }
        public float Duration { get; set; } = 5.0f;
        public int FrameRate { get; set; } = 30;
        public CameraAngle CameraAngle { get; set; }
    }

    public class PipelineDefinition;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string InputFormat { get; set; }
        public string OutputFormat { get; set; }
        public List<PipelineStepDefinition> Steps { get; set; }
    }

    // Result Classes;
    public class ConversionResult;
    {
        public bool Success { get; set; }
        public string ConvertedAnimationId { get; set; }
        public string SourceFormat { get; set; }
        public string TargetFormat { get; set; }
        public DateTime ConversionTime { get; set; }
        public long FileSize { get; set; }
        public bool FromCache { get; set; }
        public List<string> Warnings { get; set; }
    }

    public class BatchConversionResult;
    {
        public int TotalConversions { get; set; }
        public int SuccessfulConversions { get; set; }
        public int FailedConversions { get; set; }
        public List<ConversionResult> Results { get; set; }
        public List<FailedConversion> FailedItems { get; set; }
        public double TotalProcessingTime { get; set; }
    }

    public class FailedConversion;
    {
        public ConversionRequest Request { get; set; }
        public string ErrorMessage { get; set; }
        public ErrorCode ErrorCode { get; set; }
        public DateTime FailureTime { get; set; } = DateTime.UtcNow;
    }

    public class ImportResult;
    {
        public bool Success { get; set; }
        public string ImportedAnimationId { get; set; }
        public ImportStatistics ImportStatistics { get; set; }
        public List<string> Warnings { get; set; }
        public MixamoCompatibility MixamoCompatibility { get; set; }
    }

    public class ExportResult;
    {
        public bool Success { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
        public string ExportFormat { get; set; }
        public double ExportTime { get; set; }
        public UnrealCompatibility UnrealCompatibility { get; set; }
        public float CompressionRatio { get; set; }
    }

    public class RetargetResult;
    {
        public bool Success { get; set; }
        public string RetargetedAnimationId { get; set; }
        public string SourceSkeletonId { get; set; }
        public string TargetSkeletonId { get; set; }
        public RetargetingMetrics RetargetingMetrics { get; set; }
    }

    public class OptimizationResult;
    {
        public bool Success { get; set; }
        public string OptimizedAnimationId { get; set; }
        public AnimationMetrics OriginalMetrics { get; set; }
        public AnimationMetrics OptimizedMetrics { get; set; }
        public OptimizationGain OptimizationGain { get; set; }
    }

    public class FrameRateResult;
    {
        public bool Success { get; set; }
        public string ConvertedAnimationId { get; set; }
        public float SourceFrameRate { get; set; }
        public float TargetFrameRate { get; set; }
        public int OriginalFrameCount { get; set; }
        public int ConvertedFrameCount { get; set; }
        public InterpolationMethod FrameInterpolationMethod { get; set; }
    }

    public class CompressionResult;
    {
        public bool Success { get; set; }
        public string CompressedAnimationId { get; set; }
        public long OriginalSize { get; set; }
        public long CompressedSize { get; set; }
        public float CompressionRatio { get; set; }
        public CompressionMethod CompressionMethod { get; set; }
        public float QualityLoss { get; set; }
    }

    public class BakingResult;
    {
        public bool Success { get; set; }
        public string BakedAnimationId { get; set; }
        public BakingMethod BakingMethod { get; set; }
        public bool BakeToWorldSpace { get; set; }
        public bool RemoveInverseKinematics { get; set; }
        public int FrameCount { get; set; }
    }

    public class MirroringResult;
    {
        public bool Success { get; set; }
        public string MirroredAnimationId { get; set; }
        public MirrorAxis MirrorAxis { get; set; }
        public Dictionary<string, string> BoneMapping { get; set; }
        public bool PreserveOriginal { get; set; }
    }

    public class LoopingResult;
    {
        public bool Success { get; set; }
        public string LoopedAnimationId { get; set; }
        public int LoopStartFrame { get; set; }
        public int LoopEndFrame { get; set; }
        public bool SmoothTransition { get; set; }
        public int TransitionFrames { get; set; }
        public bool IsSeamlessLoop { get; set; }
    }

    public class SplittingResult;
    {
        public bool Success { get; set; }
        public string OriginalAnimationId { get; set; }
        public List<string> SplitAnimations { get; set; }
        public int SegmentCount { get; set; }
        public bool PreserveOriginal { get; set; }
    }

    public class MergingResult;
    {
        public bool Success { get; set; }
        public string MergedAnimationId { get; set; }
        public List<string> SourceAnimationIds { get; set; }
        public MergeMethod MergeMethod { get; set; }
        public int TransitionFrames { get; set; }
        public float TotalDuration { get; set; }
    }

    public class PreviewResult;
    {
        public bool Success { get; set; }
        public string PreviewPath { get; set; }
        public PreviewFormat PreviewFormat { get; set; }
        public int FrameCount { get; set; }
        public PreviewResolution Resolution { get; set; }
        public float Duration { get; set; }
    }

    // Data Classes;
    public class SourceAnimationData;
    {
        public string Format { get; set; }
        public byte[] RawData { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public List<AnimationTrack> Tracks { get; set; }
    }

    public class ConvertedAnimationData;
    {
        public string TargetFormat { get; set; }
        public byte[] BinaryData { get; set; }
        public AnimationMetadata Metadata { get; set; }
        public List<string> Warnings { get; set; }
    }

    public class AnimationData;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Format { get; set; }
        public string SkeletonId { get; set; }
        public byte[] Data { get; set; }
        public List<AnimationFrame> Frames { get; set; }
        public int BoneCount { get; set; }
        public float Duration { get; set; }
        public float FrameRate { get; set; }
        public float CompressionRatio { get; set; }
        public string SourceFormat { get; set; }
        public DateTime ImportTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class AnimationFrame;
    {
        public int FrameNumber { get; set; }
        public float Time { get; set; }
        public List<BoneTransform> BoneTransforms { get; set; }
        public Dictionary<string, float> Curves { get; set; }
    }

    public class BoneTransform;
    {
        public string BoneName { get; set; }
        public Vector3 Position { get; set; }
        public Quaternion Rotation { get; set; }
        public Vector3 Scale { get; set; }
    }

    public class AnimationTrack;
    {
        public string Name { get; set; }
        public TrackType Type { get; set; }
        public List<Keyframe> Keyframes { get; set; }
    }

    public class Keyframe;
    {
        public float Time { get; set; }
        public object Value { get; set; }
        public InterpolationType Interpolation { get; set; }
    }

    public class AnimationMetadata;
    {
        public string Creator { get; set; }
        public DateTime CreationDate { get; set; }
        public string Software { get; set; }
        public string Version { get; set; }
        public Dictionary<string, object> CustomData { get; set; }
    }

    public class SupportedFormat;
    {
        public string Format { get; set; }
        public string Extension { get; set; }
        public FormatType Type { get; set; }
        public FormatCategory Category { get; set; }
        public List<FormatOperation> SupportedOperations { get; set; }
        public List<string> Features { get; set; }
    }

    public class FormatDetails;
    {
        public string Format { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public string MimeType { get; set; }
        public List<string> SupportedFeatures { get; set; }
        public List<string> Limitations { get; set; }
        public string RecommendedUse { get; set; }
        public string SampleFile { get; set; }
    }

    public class ConversionStatistics;
    {
        public int TotalConversions { get; set; }
        public int SuccessfulConversions { get; set; }
        public int FailedConversions { get; set; }
        public double AverageConversionTime { get; set; }
        public string MostConvertedFormat { get; set; }
        public double ConversionSuccessRate { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    public class ConversionPipeline;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public List<PipelineStep> Steps { get; set; }
        public string InputFormat { get; set; }
        public string OutputFormat { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class PipelineStep;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public PipelineStepType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public int Order { get; set; }
    }

    public class PipelineStepDefinition;
    {
        public string Name { get; set; }
        public PipelineStepType Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public int Order { get; set; }
    }

    // Helper Classes;
    public class ImportStatistics;
    {
        public int FrameCount { get; set; }
        public float Duration { get; set; }
        public int BoneCount { get; set; }
        public long FileSize { get; set; }
        public double ImportTime { get; set; }
    }

    public class RetargetingMetrics;
    {
        public float BoneMappingAccuracy { get; set; }
        public float PositionError { get; set; }
        public float RotationError { get; set; }
        public double ProcessingTime { get; set; }
    }

    public class AnimationMetrics;
    {
        public int FrameCount { get; set; }
        public int BoneCount { get; set; }
        public float Duration { get; set; }
        public long MemorySize { get; set; }
        public float CompressionRatio { get; set; }
    }

    public class OptimizationGain;
    {
        public float SizeReduction { get; set; } // Percentage;
        public float PerformanceImprovement { get; set; } // Percentage;
        public double ProcessingTime { get; set; }
    }

    public class AnimationSegment;
    {
        public string Name { get; set; }
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public float StartTime { get; set; }
        public float EndTime { get; set; }
    }

    public class LoopPoints;
    {
        public int StartFrame { get; set; }
        public int EndFrame { get; set; }
        public float StartTime { get; set; }
        public float EndTime { get; set; }
        public float MatchScore { get; set; }
    }

    public class PreviewFrame;
    {
        public int FrameNumber { get; set; }
        public byte[] ImageData { get; set; }
        public float Time { get; set; }
    }

    // Enums;
    public enum FormatType;
    {
        Binary,
        Text,
        BinaryAndText;
    }

    public enum FormatCategory;
    {
        IndustryStandard,
        GameEngine,
        WebStandard,
        MotionCapture,
        Proprietary;
    }

    public enum FormatOperation;
    {
        Import,
        Export,
        Convert,
        Optimize;
    }

    public enum OptimizationLevel;
    {
        None,
        Low,
        Medium,
        High,
        Maximum;
    }

    public enum CompressionLevel;
    {
        None,
        Low,
        Medium,
        High,
        Lossless;
    }

    public enum InterpolationMethod;
    {
        Linear,
        Bezier,
        Cubic,
        Step,
        Smooth;
    }

    public enum RotationOrder;
    {
        XYZ,
        XZY,
        YXZ,
        YZX,
        ZXY,
        ZYX;
    }

    public enum UnityRigType;
    {
        Humanoid,
        Generic,
        Legacy;
    }

    public enum UnrealSkeletonType;
    {
        Humanoid,
        Quadruped,
        Custom;
    }

    public enum GodotAnimationType;
    {
        AnimationPlayer,
        AnimationTree,
        Tween;
    }

    public enum NedaCompressionType;
    {
        None,
        Low,
        Medium,
        High;
    }

    public enum CompressionMethod;
    {
        Quantization,
        KeyframeReduction,
        CurveFitting,
        Wavelet,
        Custom;
    }

    public enum BakingMethod;
    {
        WorldSpace,
        LocalSpace,
        RemoveConstraints,
        SimplifyHierarchy;
    }

    public enum MirrorAxis;
    {
        X,
        Y,
        Z,
        XY,
        XZ,
        YZ;
    }

    public enum LoopDetectionMethod;
    {
        Automatic,
        Manual,
        PatternMatching;
    }

    public enum MergeMethod;
    {
        Sequential,
        Blended,
        Layered,
        Additive;
    }

    public enum PreviewFormat;
    {
        Video,
        Gif,
        ImageSequence;
    }

    public enum PreviewResolution;
    {
        SD = 480,
        HD = 720,
        FullHD = 1080,
        QuadHD = 1440,
        UltraHD = 2160;
    }

    public enum CameraAngle;
    {
        Front,
        Side,
        Top,
        Perspective,
        Follow;
    }

    public enum PipelineStepType;
    {
        Import,
        Convert,
        Optimize,
        Compress,
        Retarget,
        Export;
    }

    public enum TrackType;
    {
        Position,
        Rotation,
        Scale,
        Float,
        Vector3,
        Color;
    }

    public enum InterpolationType;
    {
        Constant,
        Linear,
        Bezier,
        Hermite;
    }

    // Compatibility Classes;
    public class MixamoCompatibility;
    {
        public bool IsCompatible { get; set; }
        public List<string> CompatibleFeatures { get; set; }
        public List<string> IncompatibleFeatures { get; set; }
        public float CompatibilityScore { get; set; }
    }

    public class UnrealCompatibility;
    {
        public bool IsCompatible { get; set; }
        public string RecommendedSkeleton { get; set; }
        public List<string> RequiredFixes { get; set; }
        public float CompatibilityScore { get; set; }
    }

    // Internal Data Classes;
    internal class ConversionStats;
    {
        public int TotalConversions { get; set; }
        public int SuccessfulConversions { get; set; }
        public int FailedConversions { get; set; }
        public Dictionary<string, int> FormatPairCounts { get; set; } = new Dictionary<string, int>();
    }

    // Math Structs;
    public struct Vector3;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }
    }

    public struct Quaternion;
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; }

        public Quaternion(float x, float y, float z, float w)
        {
            X = x;
            Y = y;
            Z = z;
            W = w;
        }
    }

    #endregion;
}
