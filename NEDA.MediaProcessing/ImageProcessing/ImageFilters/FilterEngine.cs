using Microsoft.Identity.Client;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.MediaProcessing.ImageProcessing.ImageFilters.Filters;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.MediaProcessing.ImageProcessing.ImageFilters;
{
    /// <summary>
    /// Görüntü filtreleme motoru - Endüstriyel seviye;
    /// Çoklu filtre türü, GPU hızlandırma, pipeline işleme ve önbellekleme desteği;
    /// </summary>
    public interface IFilterEngine;
    {
        /// <summary>
        /// Görüntüye filtre uygular;
        /// </summary>
        /// <param name="image">Giriş görüntüsü</param>
        /// <param name="filter">Uygulanacak filtre</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Filtrelenmiş görüntü</returns>
        Task<ProcessedImage> ApplyFilterAsync(ImageData image, ImageFilter filter, CancellationToken cancellationToken = default);

        /// <summary>
        /// Görüntüye filtre pipeline'ı uygular;
        /// </summary>
        /// <param name="image">Giriş görüntüsü</param>
        /// <param name="filters">Filtre pipeline'ı</param>
        /// <param name="optimizePipeline">Pipeline optimizasyonu</param>
        /// <returns>İşlenmiş görüntü</returns>
        Task<ProcessedImage> ApplyFilterPipelineAsync(ImageData image, IEnumerable<ImageFilter> filters, bool optimizePipeline = true);

        /// <summary>
        /// Batch görüntü filtreleme;
        /// </summary>
        /// <param name="images">Giriş görüntüleri</param>
        /// <param name="filter">Uygulanacak filtre</param>
        /// <param name="parallelProcessing">Paralel işleme</param>
        /// <returns>İşlenmiş görüntüler</returns>
        Task<BatchFilterResult> ApplyBatchFilterAsync(IEnumerable<ImageData> images, ImageFilter filter, bool parallelProcessing = true);

        /// <summary>
        /// Filtre performansını test eder;
        /// </summary>
        /// <param name="filter">Test edilecek filtre</param>
        /// <param name="testImage">Test görüntüsü</param>
        /// <returns>Performans metrikleri</returns>
        Task<FilterPerformanceMetrics> TestFilterPerformanceAsync(ImageFilter filter, ImageData testImage);

        /// <summary>
        /// Filtreyi önbelleğe kaydeder;
        /// </summary>
        /// <param name="filter">Kaydedilecek filtre</param>
        Task CacheFilterAsync(ImageFilter filter);

        /// <summary>
        /// GPU hızlandırmayı ayarlar;
        /// </summary>
        /// <param name="enable">GPU hızlandırma durumu</param>
        Task SetGpuAccelerationAsync(bool enable);

        /// <summary>
        /// Özel filtre kaydeder;
        /// </summary>
        /// <param name="filter">Kaydedilecek özel filtre</param>
        Task RegisterCustomFilterAsync(CustomFilter filter);

        /// <summary>
        /// Kullanılabilir filtreleri getirir;
        /// </summary>
        /// <returns>Filtre listesi</returns>
        Task<IEnumerable<FilterInfo>> GetAvailableFiltersAsync();

        /// <summary>
        /// Filtre önizlemesi oluşturur;
        /// </summary>
        /// <param name="image">Kaynak görüntü</param>
        /// <param name="filter">Filtre</param>
        /// <param name="previewSize">Önizleme boyutu</param>
        /// <returns>Önizleme görüntüsü</returns>
        Task<ImageData> GenerateFilterPreviewAsync(ImageData image, ImageFilter filter, Size previewSize);
    }

    /// <summary>
    /// Filtre motoru - Endüstriyel implementasyon;
    /// </summary>
    public class FilterEngine : IFilterEngine, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IAppConfig _appConfig;
        private readonly FilterCache _filterCache;
        private readonly FilterRegistry _filterRegistry
        private readonly GpuAccelerationManager _gpuManager;
        private readonly PipelineOptimizer _pipelineOptimizer;
        private readonly ConcurrentDictionary<string, IFilterProcessor> _filterProcessors;
        private readonly SemaphoreSlim _concurrencySemaphore;
        private readonly int _maxConcurrentOperations;
        private readonly bool _enableGpuAcceleration;
        private bool _disposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// FilterEngine constructor - Dependency Injection;
        /// </summary>
        public FilterEngine(
            ILogger logger,
            IEventBus eventBus,
            IAppConfig appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            _filterCache = new FilterCache(logger);
            _filterRegistry = new FilterRegistry(logger);
            _gpuManager = new GpuAccelerationManager(logger);
            _pipelineOptimizer = new PipelineOptimizer(logger);
            _filterProcessors = new ConcurrentDictionary<string, IFilterProcessor>();

            _maxConcurrentOperations = appConfig.GetSetting<int>("FilterEngine:MaxConcurrentOperations",
                Environment.ProcessorCount * 2);
            _concurrencySemaphore = new SemaphoreSlim(_maxConcurrentOperations, _maxConcurrentOperations);

            _enableGpuAcceleration = appConfig.GetSetting<bool>("FilterEngine:EnableGpuAcceleration", false);

            InitializeFilterProcessors();
            InitializeBuiltInFilters();

            _logger.Info("FilterEngine initialized successfully");
            _logger.Info($"Max concurrent operations: {_maxConcurrentOperations}, GPU acceleration: {_enableGpuAcceleration}");
        }

        /// <summary>
        /// Görüntüye filtre uygular;
        /// </summary>
        public async Task<ProcessedImage> ApplyFilterAsync(ImageData image, ImageFilter filter, CancellationToken cancellationToken = default)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            var operationId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                ValidateImage(image);
                ValidateFilter(filter);

                _logger.Info($"Applying filter: {filter.Name} (ID: {operationId})");
                _logger.Debug($"Image: {image.Width}x{image.Height}, Format: {image.Format}, Filter type: {filter.Type}");

                // Önbellek kontrolü;
                var cacheKey = GenerateCacheKey(image, filter);
                if (_filterCache.TryGet(cacheKey, out var cachedResult))
                {
                    _logger.Debug($"Returning cached filter result for: {filter.Name}");
                    cachedResult.Metadata["Cached"] = true;
                    cachedResult.Metadata["CacheHit"] = true;
                    return cachedResult;
                }

                // Filtre işlemcisini al;
                var processor = GetFilterProcessor(filter.Type);

                // GPU hızlandırma kontrolü;
                var useGpu = _enableGpuAcceleration && processor.SupportsGpuAcceleration;
                if (useGpu)
                {
                    await _gpuManager.AcquireGpuContextAsync();
                }

                await _concurrencySemaphore.WaitAsync(cancellationToken);

                try
                {
                    // Filtreyi uygula;
                    var filterContext = new FilterContext;
                    {
                        Image = image,
                        Filter = filter,
                        OperationId = operationId,
                        UseGpuAcceleration = useGpu,
                        CancellationToken = cancellationToken;
                    };

                    var result = await processor.ProcessAsync(filterContext);

                    // Sonuçları paketle;
                    var processedImage = new ProcessedImage;
                    {
                        OperationId = operationId,
                        OriginalImage = image,
                        FilteredImage = result.ProcessedImage,
                        FilterName = filter.Name,
                        FilterType = filter.Type,
                        ProcessingTime = DateTime.UtcNow - startTime,
                        PerformanceMetrics = result.Metrics,
                        Success = result.Success,
                        ErrorMessage = result.ErrorMessage,
                        Metadata = new Dictionary<string, object>
                        {
                            ["FilterParameters"] = filter.Parameters,
                            ["UseGpu"] = useGpu,
                            ["ProcessorType"] = processor.GetType().Name,
                            ["ImageSize"] = $"{image.Width}x{image.Height}",
                            ["OutputFormat"] = result.ProcessedImage?.Format;
                        }
                    };

                    // Önbelleğe ekle;
                    if (result.Success && filter.Cacheable)
                    {
                        _filterCache.Add(cacheKey, processedImage);
                        processedImage.Metadata["Cached"] = true;
                    }

                    // Performans metriklerini güncelle;
                    UpdatePerformanceMetrics(filter, processedImage.ProcessingTime, result.Metrics);

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new FilterAppliedEvent;
                    {
                        OperationId = operationId,
                        FilterName = filter.Name,
                        FilterType = filter.Type,
                        ImageSize = $"{image.Width}x{image.Height}",
                        ProcessingTime = processedImage.ProcessingTime,
                        Success = result.Success,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Info($"Filter applied successfully: {filter.Name}");
                    _logger.Debug($"Processing time: {processedImage.ProcessingTime.TotalMilliseconds:F2}ms, " +
                                $"Success: {result.Success}, GPU: {useGpu}");

                    return processedImage;
                }
                finally
                {
                    _concurrencySemaphore.Release();

                    if (useGpu)
                    {
                        _gpuManager.ReleaseGpuContext();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _logger.Warn($"Filter operation cancelled: {operationId}");
                await HandleCancellationAsync(operationId, startTime, filter.Name);
                throw;
            }
            catch (FilterProcessingException ex)
            {
                _logger.Error($"Filter processing failed: {ex.Message}", ex);
                await HandleFailureAsync(operationId, startTime, filter.Name, ex);
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Unexpected error in filter application: {ex.Message}", ex);
                await HandleFailureAsync(operationId, startTime, filter.Name, ex);
                throw new FilterEngineException($"Filter application failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Görüntüye filtre pipeline'ı uygular;
        /// </summary>
        public async Task<ProcessedImage> ApplyFilterPipelineAsync(
            ImageData image,
            IEnumerable<ImageFilter> filters,
            bool optimizePipeline = true)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (filters == null)
                throw new ArgumentNullException(nameof(filters));

            var pipelineId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                var filterList = filters.ToList();
                if (!filterList.Any())
                    throw new ArgumentException("Filter pipeline cannot be empty", nameof(filters));

                _logger.Info($"Applying filter pipeline: {pipelineId}");
                _logger.Info($"Pipeline length: {filterList.Count}, Optimize: {optimizePipeline}");

                // Pipeline optimizasyonu;
                var optimizedFilters = optimizePipeline;
                    ? await _pipelineOptimizer.OptimizePipelineAsync(filterList)
                    : filterList;

                if (optimizedFilters.Count != filterList.Count)
                {
                    _logger.Debug($"Pipeline optimized: {filterList.Count} -> {optimizedFilters.Count} filters");
                }

                // Pipeline yürütme;
                var currentImage = image;
                var appliedFilters = new List<AppliedFilterInfo>();
                var pipelineMetrics = new PipelineMetrics();

                foreach (var filter in optimizedFilters)
                {
                    var filterStartTime = DateTime.UtcNow;

                    try
                    {
                        _logger.Debug($"Applying pipeline step: {filter.Name}");

                        var result = await ApplyFilterAsync(currentImage, filter);

                        if (!result.Success)
                        {
                            _logger.Warn($"Pipeline step failed: {filter.Name}");
                            pipelineMetrics.FailedSteps++;

                            if (filter.FailureAction == FilterFailureAction.StopPipeline)
                            {
                                throw new PipelineExecutionException($"Pipeline stopped due to failure in filter: {filter.Name}");
                            }
                            // Continue: bir sonraki filtreye devam et;
                            continue;
                        }

                        currentImage = result.FilteredImage;

                        appliedFilters.Add(new AppliedFilterInfo;
                        {
                            FilterName = filter.Name,
                            FilterType = filter.Type,
                            ProcessingTime = result.ProcessingTime,
                            PerformanceMetrics = result.PerformanceMetrics;
                        });

                        pipelineMetrics.TotalProcessingTime += result.ProcessingTime;
                        pipelineMetrics.SuccessfulSteps++;

                        // Ara sonuçları cache'le (opsiyonel)
                        if (filter.CacheIntermediateResults)
                        {
                            var intermediateKey = GeneratePipelineCacheKey(image, filter, appliedFilters.Count);
                            _filterCache.Add(intermediateKey, result);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Pipeline step execution failed: {filter.Name} - {ex.Message}", ex);
                        pipelineMetrics.FailedSteps++;

                        if (filter.FailureAction == FilterFailureAction.StopPipeline)
                        {
                            throw new PipelineExecutionException($"Pipeline execution failed at filter: {filter.Name}", ex);
                        }
                    }
                }

                // Pipeline sonucunu oluştur;
                var pipelineResult = new ProcessedImage;
                {
                    OperationId = pipelineId,
                    OriginalImage = image,
                    FilteredImage = currentImage,
                    FilterName = "FilterPipeline",
                    FilterType = FilterType.CustomPipeline,
                    ProcessingTime = DateTime.UtcNow - startTime,
                    Success = pipelineMetrics.FailedSteps == 0,
                    Metadata = new Dictionary<string, object>
                    {
                        ["PipelineId"] = pipelineId,
                        ["TotalFilters"] = filterList.Count,
                        ["AppliedFilters"] = appliedFilters.Count,
                        ["FailedFilters"] = pipelineMetrics.FailedSteps,
                        ["Optimized"] = optimizePipeline,
                        ["AppliedFilterList"] = appliedFilters.Select(f => f.FilterName).ToList(),
                        ["PipelineMetrics"] = pipelineMetrics;
                    }
                };

                // Olay yayınla;
                await _eventBus.PublishAsync(new FilterPipelineCompletedEvent;
                {
                    PipelineId = pipelineId,
                    TotalFilters = filterList.Count,
                    SuccessfulSteps = pipelineMetrics.SuccessfulSteps,
                    FailedSteps = pipelineMetrics.FailedSteps,
                    TotalProcessingTime = pipelineResult.ProcessingTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Filter pipeline completed: {pipelineId}");
                _logger.Info($"Success: {pipelineResult.Success}, " +
                           $"Applied: {appliedFilters.Count}/{filterList.Count}, " +
                           $"Total time: {pipelineResult.ProcessingTime.TotalSeconds:F2}s");

                return pipelineResult;
            }
            catch (PipelineExecutionException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Filter pipeline execution failed: {ex.Message}", ex);
                throw new FilterEngineException($"Filter pipeline execution failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Batch görüntü filtreleme;
        /// </summary>
        public async Task<BatchFilterResult> ApplyBatchFilterAsync(
            IEnumerable<ImageData> images,
            ImageFilter filter,
            bool parallelProcessing = true)
        {
            if (images == null)
                throw new ArgumentNullException(nameof(images));
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            var batchId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                var imageList = images.ToList();
                if (!imageList.Any())
                    throw new ArgumentException("Image collection cannot be empty", nameof(images));

                _logger.Info($"Starting batch filter operation: {batchId}");
                _logger.Info($"Total images: {imageList.Count}, Filter: {filter.Name}, Parallel: {parallelProcessing}");

                var results = new List<ProcessedImage>();
                var batchMetrics = new BatchMetrics();

                if (parallelProcessing)
                {
                    // Paralel işleme;
                    var parallelOptions = new ParallelOptions;
                    {
                        MaxDegreeOfParallelism = _maxConcurrentOperations,
                        CancellationToken = CancellationToken.None;
                    };

                    var concurrentResults = new ConcurrentBag<ProcessedImage>();

                    await Parallel.ForEachAsync(imageList, parallelOptions, async (image, cancellationToken) =>
                    {
                        try
                        {
                            var result = await ApplyFilterAsync(image, filter, cancellationToken);
                            concurrentResults.Add(result);

                            Interlocked.Increment(ref batchMetrics.ProcessedImages);

                            if (result.Success)
                            {
                                Interlocked.Increment(ref batchMetrics.SuccessfulImages);
                            }
                            else;
                            {
                                Interlocked.Increment(ref batchMetrics.FailedImages);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Batch image processing failed: {ex.Message}", ex);
                            Interlocked.Increment(ref batchMetrics.FailedImages);
                        }
                    });

                    results = concurrentResults.ToList();
                }
                else;
                {
                    // Sıralı işleme;
                    foreach (var image in imageList)
                    {
                        try
                        {
                            var result = await ApplyFilterAsync(image, filter);
                            results.Add(result);
                            batchMetrics.ProcessedImages++;

                            if (result.Success)
                            {
                                batchMetrics.SuccessfulImages++;
                            }
                            else;
                            {
                                batchMetrics.FailedImages++;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Batch image processing failed: {ex.Message}", ex);
                            batchMetrics.FailedImages++;
                        }
                    }
                }

                // Batch sonucunu oluştur;
                var batchResult = new BatchFilterResult;
                {
                    BatchId = batchId,
                    FilterName = filter.Name,
                    FilterType = filter.Type,
                    StartTime = startTime,
                    EndTime = DateTime.UtcNow,
                    TotalProcessingTime = DateTime.UtcNow - startTime,
                    ProcessedImages = results,
                    TotalImages = imageList.Count,
                    SuccessfulImages = batchMetrics.SuccessfulImages,
                    FailedImages = batchMetrics.FailedImages,
                    SuccessRate = (double)batchMetrics.SuccessfulImages / imageList.Count,
                    ParallelProcessing = parallelProcessing,
                    PerformanceMetrics = CalculateBatchPerformanceMetrics(results)
                };

                // Olay yayınla;
                await _eventBus.PublishAsync(new BatchFilterCompletedEvent;
                {
                    BatchId = batchId,
                    FilterName = filter.Name,
                    TotalImages = imageList.Count,
                    SuccessfulImages = batchMetrics.SuccessfulImages,
                    FailedImages = batchMetrics.FailedImages,
                    TotalProcessingTime = batchResult.TotalProcessingTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Batch filter operation completed: {batchId}");
                _logger.Info($"Successful: {batchMetrics.SuccessfulImages}, " +
                           $"Failed: {batchMetrics.FailedImages}, " +
                           $"Success rate: {batchResult.SuccessRate:P2}, " +
                           $"Total time: {batchResult.TotalProcessingTime.TotalSeconds:F2}s");

                return batchResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Batch filter operation failed: {ex.Message}", ex);
                throw new FilterEngineException($"Batch filter operation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filtre performansını test eder;
        /// </summary>
        public async Task<FilterPerformanceMetrics> TestFilterPerformanceAsync(ImageFilter filter, ImageData testImage)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));
            if (testImage == null)
                throw new ArgumentNullException(nameof(testImage));

            try
            {
                _logger.Info($"Testing filter performance: {filter.Name}");

                var metrics = new FilterPerformanceMetrics;
                {
                    FilterName = filter.Name,
                    FilterType = filter.Type,
                    TestImageSize = $"{testImage.Width}x{testImage.Height}",
                    TestTimestamp = DateTime.UtcNow;
                };

                // Çoklu test iterasyonları;
                const int iterations = 10;
                var executionTimes = new List<TimeSpan>();
                var memoryUsages = new List<long>();

                for (int i = 0; i < iterations; i++)
                {
                    _logger.Debug($"Performance test iteration {i + 1}/{iterations}");

                    var startTime = DateTime.UtcNow;
                    var initialMemory = GC.GetTotalMemory(false);

                    var result = await ApplyFilterAsync(testImage, filter);

                    var endTime = DateTime.UtcNow;
                    var finalMemory = GC.GetTotalMemory(false);

                    if (result.Success)
                    {
                        executionTimes.Add(result.ProcessingTime);
                        memoryUsages.Add(finalMemory - initialMemory);

                        if (result.PerformanceMetrics != null)
                        {
                            metrics.GpuAccelerationUsed = result.Metadata.ContainsKey("UseGpu") &&
                                                         (bool)result.Metadata["UseGpu"];
                            metrics.ProcessorType = result.Metadata["ProcessorType"] as string;
                        }
                    }
                    else;
                    {
                        _logger.Warn($"Filter failed during performance test iteration {i + 1}");
                    }

                    // Her iterasyon arasında küçük bir gecikme;
                    await Task.Delay(100);
                }

                // Metrikleri hesapla;
                if (executionTimes.Any())
                {
                    metrics.AverageExecutionTime = TimeSpan.FromMilliseconds(
                        executionTimes.Average(t => t.TotalMilliseconds));
                    metrics.MinExecutionTime = TimeSpan.FromMilliseconds(
                        executionTimes.Min(t => t.TotalMilliseconds));
                    metrics.MaxExecutionTime = TimeSpan.FromMilliseconds(
                        executionTimes.Max(t => t.TotalMilliseconds));
                    metrics.StandardDeviation = CalculateStandardDeviation(
                        executionTimes.Select(t => t.TotalMilliseconds));
                }

                if (memoryUsages.Any())
                {
                    metrics.AverageMemoryUsage = memoryUsages.Average();
                    metrics.PeakMemoryUsage = memoryUsages.Max();
                }

                // Performans değerlendirmesi;
                metrics.PerformanceScore = CalculatePerformanceScore(metrics);
                metrics.Recommendations = GeneratePerformanceRecommendations(metrics);

                _logger.Info($"Filter performance test completed: {filter.Name}");
                _logger.Info($"Average time: {metrics.AverageExecutionTime.TotalMilliseconds:F2}ms, " +
                           $"Score: {metrics.PerformanceScore}/100");

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.Error($"Filter performance test failed: {ex.Message}", ex);
                throw new FilterEngineException($"Filter performance test failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filtreyi önbelleğe kaydeder;
        /// </summary>
        public async Task CacheFilterAsync(ImageFilter filter)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            try
            {
                // Filtre parametrelerini optimize et;
                var optimizedFilter = await OptimizeFilterForCachingAsync(filter);

                // Önbelleğe ekle;
                _filterCache.CacheFilter(optimizedFilter);

                // Filtre işlemcisini ön-yükle;
                var processor = GetFilterProcessor(filter.Type);
                await processor.PreloadAsync(filter);

                _logger.Info($"Filter cached successfully: {filter.Name}");

                // Olay yayınla;
                await _eventBus.PublishAsync(new FilterCachedEvent;
                {
                    FilterName = filter.Name,
                    FilterType = filter.Type,
                    CacheTimestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to cache filter: {ex.Message}", ex);
                throw new FilterEngineException($"Failed to cache filter: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// GPU hızlandırmayı ayarlar;
        /// </summary>
        public async Task SetGpuAccelerationAsync(bool enable)
        {
            lock (_syncLock)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(FilterEngine));

                if (enable && !_gpuManager.IsGpuAvailable())
                {
                    throw new GpuAccelerationException("GPU acceleration is not available on this system");
                }
            }

            try
            {
                await _gpuManager.SetGpuAccelerationAsync(enable);

                _logger.Info($"GPU acceleration {(enable ? "enabled" : "disabled")}");

                // Tüm işlemcileri güncelle;
                foreach (var processor in _filterProcessors.Values)
                {
                    if (processor.SupportsGpuAcceleration)
                    {
                        await processor.SetGpuAccelerationAsync(enable);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to set GPU acceleration: {ex.Message}", ex);
                throw new FilterEngineException($"Failed to set GPU acceleration: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Özel filtre kaydeder;
        /// </summary>
        public async Task RegisterCustomFilterAsync(CustomFilter filter)
        {
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            try
            {
                ValidateCustomFilter(filter);

                // Filtreyi kayıt defterine ekle;
                await _filterRegistry.RegisterFilterAsync(filter);

                // Özel filtre işlemcisi oluştur;
                var processor = new CustomFilterProcessor(filter, _logger);
                _filterProcessors[filter.Name] = processor;

                _logger.Info($"Custom filter registered: {filter.Name}, Type: {filter.Type}");

                // Olay yayınla;
                await _eventBus.PublishAsync(new CustomFilterRegisteredEvent;
                {
                    FilterName = filter.Name,
                    FilterType = filter.Type,
                    Author = filter.Author,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to register custom filter: {ex.Message}", ex);
                throw new FilterEngineException($"Failed to register custom filter: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Kullanılabilir filtreleri getirir;
        /// </summary>
        public async Task<IEnumerable<FilterInfo>> GetAvailableFiltersAsync()
        {
            try
            {
                var filters = await _filterRegistry.GetAllFiltersAsync();

                // Performans metriklerini ekle;
                foreach (var filter in filters)
                {
                    filter.PerformanceMetrics = GetFilterPerformanceMetrics(filter.Name);
                }

                return filters;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get available filters: {ex.Message}", ex);
                throw new FilterEngineException($"Failed to get available filters: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Filtre önizlemesi oluşturur;
        /// </summary>
        public async Task<ImageData> GenerateFilterPreviewAsync(ImageData image, ImageFilter filter, Size previewSize)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));
            if (filter == null)
                throw new ArgumentNullException(nameof(filter));

            try
            {
                _logger.Debug($"Generating filter preview: {filter.Name}, Size: {previewSize}");

                // Görüntüyü önizleme boyutuna ölçeklendir;
                var previewImage = await ResizeImageAsync(image, previewSize.Width, previewSize.Height);

                // Filtreyi uygula;
                var result = await ApplyFilterAsync(previewImage, filter);

                if (!result.Success)
                {
                    throw new FilterProcessingException($"Failed to generate preview for filter: {filter.Name}");
                }

                return result.FilteredImage;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate filter preview: {ex.Message}", ex);
                throw new FilterEngineException($"Failed to generate filter preview: {ex.Message}", ex);
            }
        }

        #region Private Methods;

        private void InitializeFilterProcessors()
        {
            try
            {
                // Yerleşik filtre işlemcilerini kaydet;
                RegisterFilterProcessor(FilterType.Blur, new BlurFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Sharpen, new SharpenFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.EdgeDetection, new EdgeDetectionFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.ColorCorrection, new ColorCorrectionFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.NoiseReduction, new NoiseReductionFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.BrightnessContrast, new BrightnessContrastFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Saturation, new SaturationFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.HueRotate, new HueRotateFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Sepia, new SepiaFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Grayscale, new GrayscaleFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Invert, new InvertFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Threshold, new ThresholdFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Emboss, new EmbossFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.GaussianBlur, new GaussianBlurFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.MotionBlur, new MotionBlurFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.ZoomBlur, new ZoomBlurFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.OilPainting, new OilPaintingFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Watercolor, new WatercolorFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Cartoon, new CartoonFilterProcessor(_logger));
                RegisterFilterProcessor(FilterType.Vignette, new VignetteFilterProcessor(_logger));

                _logger.Info($"Initialized {_filterProcessors.Count} filter processors");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize filter processors: {ex.Message}", ex);
                throw new FilterEngineException($"Failed to initialize filter processors: {ex.Message}", ex);
            }
        }

        private void InitializeBuiltInFilters()
        {
            try
            {
                // Yerleşik filtre tanımları;
                var builtInFilters = new List<ImageFilter>
                {
                    CreateBlurFilter("GaussianBlur", 5.0),
                    CreateSharpenFilter("Sharpen", 1.0),
                    CreateEdgeDetectionFilter("SobelEdge"),
                    CreateColorCorrectionFilter("AutoColor"),
                    CreateNoiseReductionFilter("MedianFilter", 3),
                    CreateBrightnessContrastFilter("BrightnessBoost", 20, 10),
                    CreateSaturationFilter("SaturationBoost", 1.5),
                    CreateGrayscaleFilter("Grayscale"),
                    CreateSepiaFilter("Sepia"),
                    CreateInvertFilter("Invert"),
                    CreateEmbossFilter("Emboss"),
                    CreateVignetteFilter("Vignette", 0.8)
                };

                foreach (var filter in builtInFilters)
                {
                    _filterRegistry.RegisterFilterAsync(filter).Wait();
                }

                _logger.Info($"Initialized {builtInFilters.Count} built-in filters");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize built-in filters: {ex.Message}", ex);
            }
        }

        private void RegisterFilterProcessor(FilterType filterType, IFilterProcessor processor)
        {
            var key = filterType.ToString();
            _filterProcessors[key] = processor;

            // GPU hızlandırmayı ayarla;
            if (_enableGpuAcceleration && processor.SupportsGpuAcceleration)
            {
                processor.SetGpuAccelerationAsync(true).Wait();
            }
        }

        private IFilterProcessor GetFilterProcessor(FilterType filterType)
        {
            var key = filterType.ToString();

            if (_filterProcessors.TryGetValue(key, out var processor))
            {
                return processor;
            }

            // Özel filtre işlemcisi kontrolü;
            var customKey = filterType.ToString();
            if (_filterProcessors.TryGetValue(customKey, out var customProcessor))
            {
                return customProcessor;
            }

            throw new FilterProcessorNotFoundException($"Filter processor not found for type: {filterType}");
        }

        private void ValidateImage(ImageData image)
        {
            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentException("Image dimensions must be positive", nameof(image));

            if (image.Data == null || image.Data.Length == 0)
                throw new ArgumentException("Image data cannot be empty", nameof(image));

            if (string.IsNullOrEmpty(image.Format))
                throw new ArgumentException("Image format must be specified", nameof(image));
        }

        private void ValidateFilter(ImageFilter filter)
        {
            if (string.IsNullOrWhiteSpace(filter.Name))
                throw new ArgumentException("Filter name cannot be empty", nameof(filter));

            if (!Enum.IsDefined(typeof(FilterType), filter.Type))
                throw new ArgumentException($"Invalid filter type: {filter.Type}", nameof(filter));
        }

        private void ValidateCustomFilter(CustomFilter filter)
        {
            ValidateFilter(filter);

            if (string.IsNullOrWhiteSpace(filter.Author))
                throw new ArgumentException("Custom filter author cannot be empty", nameof(filter));

            if (filter.Implementation == null)
                throw new ArgumentException("Custom filter implementation cannot be null", nameof(filter));
        }

        private string GenerateCacheKey(ImageData image, ImageFilter filter)
        {
            var imageHash = ComputeImageHash(image);
            var filterHash = ComputeFilterHash(filter);

            return $"{imageHash}_{filterHash}";
        }

        private string GeneratePipelineCacheKey(ImageData image, ImageFilter filter, int stepIndex)
        {
            var baseKey = GenerateCacheKey(image, filter);
            return $"{baseKey}_step{stepIndex}";
        }

        private string ComputeImageHash(ImageData image)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(image.Data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private string ComputeFilterHash(ImageFilter filter)
        {
            var filterData = $"{filter.Name}_{filter.Type}_{filter.Version}";

            if (filter.Parameters != null)
            {
                foreach (var param in filter.Parameters.OrderBy(p => p.Key))
                {
                    filterData += $"_{param.Key}:{param.Value}";
                }
            }

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(filterData));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private async Task HandleCancellationAsync(Guid operationId, DateTime startTime, string filterName)
        {
            await _eventBus.PublishAsync(new FilterOperationCancelledEvent;
            {
                OperationId = operationId,
                FilterName = filterName,
                Duration = DateTime.UtcNow - startTime,
                Timestamp = DateTime.UtcNow;
            });
        }

        private async Task HandleFailureAsync(Guid operationId, DateTime startTime, string filterName, Exception exception)
        {
            await _eventBus.PublishAsync(new FilterOperationFailedEvent;
            {
                OperationId = operationId,
                FilterName = filterName,
                ErrorMessage = exception.Message,
                ExceptionType = exception.GetType().Name,
                Duration = DateTime.UtcNow - startTime,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void UpdatePerformanceMetrics(ImageFilter filter, TimeSpan processingTime, FilterMetrics metrics)
        {
            // Performans metriklerini güncelle;
            var filterMetrics = GetFilterPerformanceMetrics(filter.Name);

            filterMetrics.TotalExecutions++;
            filterMetrics.TotalProcessingTime += processingTime;
            filterMetrics.AverageExecutionTime = TimeSpan.FromMilliseconds(
                filterMetrics.TotalProcessingTime.TotalMilliseconds / filterMetrics.TotalExecutions);

            if (processingTime < filterMetrics.MinExecutionTime || filterMetrics.MinExecutionTime == TimeSpan.Zero)
            {
                filterMetrics.MinExecutionTime = processingTime;
            }

            if (processingTime > filterMetrics.MaxExecutionTime)
            {
                filterMetrics.MaxExecutionTime = processingTime;
            }

            if (metrics != null)
            {
                filterMetrics.TotalMemoryUsage += metrics.MemoryUsage;
                filterMetrics.AverageMemoryUsage = filterMetrics.TotalMemoryUsage / filterMetrics.TotalExecutions;

                if (metrics.GpuAccelerationUsed)
                {
                    filterMetrics.GpuExecutions++;
                }
            }

            // Güncellenmiş metrikleri kaydet;
            SaveFilterPerformanceMetrics(filter.Name, filterMetrics);
        }

        private FilterPerformanceInfo GetFilterPerformanceMetrics(string filterName)
        {
            // Gerçek implementasyonda kalıcı depolama kullanılır;
            return new FilterPerformanceInfo;
            {
                FilterName = filterName,
                LastExecution = DateTime.UtcNow;
            };
        }

        private void SaveFilterPerformanceMetrics(string filterName, FilterPerformanceInfo metrics)
        {
            // Gerçek implementasyonda kalıcı depolama kullanılır;
        }

        private BatchPerformanceMetrics CalculateBatchPerformanceMetrics(List<ProcessedImage> results)
        {
            var metrics = new BatchPerformanceMetrics();

            if (!results.Any())
                return metrics;

            var successfulResults = results.Where(r => r.Success).ToList();

            if (successfulResults.Any())
            {
                metrics.AverageProcessingTime = TimeSpan.FromMilliseconds(
                    successfulResults.Average(r => r.ProcessingTime.TotalMilliseconds));

                metrics.TotalProcessingTime = TimeSpan.FromMilliseconds(
                    successfulResults.Sum(r => r.ProcessingTime.TotalMilliseconds));

                metrics.MinProcessingTime = TimeSpan.FromMilliseconds(
                    successfulResults.Min(r => r.ProcessingTime.TotalMilliseconds));

                metrics.MaxProcessingTime = TimeSpan.FromMilliseconds(
                    successfulResults.Max(r => r.ProcessingTime.TotalMilliseconds));

                // Memory kullanımı;
                var memoryUsages = successfulResults;
                    .Where(r => r.PerformanceMetrics != null)
                    .Select(r => r.PerformanceMetrics.MemoryUsage)
                    .ToList();

                if (memoryUsages.Any())
                {
                    metrics.AverageMemoryUsage = memoryUsages.Average();
                    metrics.PeakMemoryUsage = memoryUsages.Max();
                }

                // GPU kullanımı;
                metrics.GpuAcceleratedOperations = successfulResults;
                    .Count(r => r.Metadata.ContainsKey("UseGpu") && (bool)r.Metadata["UseGpu"]);
            }

            return metrics;
        }

        private double CalculateStandardDeviation(IEnumerable<double> values)
        {
            var valueList = values.ToList();
            if (valueList.Count < 2)
                return 0;

            var avg = valueList.Average();
            var sum = valueList.Sum(v => Math.Pow(v - avg, 2));
            return Math.Sqrt(sum / (valueList.Count - 1));
        }

        private int CalculatePerformanceScore(FilterPerformanceMetrics metrics)
        {
            var score = 100;

            // Yürütme zamanı puanlaması;
            if (metrics.AverageExecutionTime.TotalMilliseconds > 1000)
                score -= 30;
            else if (metrics.AverageExecutionTime.TotalMilliseconds > 500)
                score -= 20;
            else if (metrics.AverageExecutionTime.TotalMilliseconds > 200)
                score -= 10;

            // Memory kullanımı puanlaması;
            if (metrics.AverageMemoryUsage > 100 * 1024 * 1024) // 100MB;
                score -= 20;
            else if (metrics.AverageMemoryUsage > 50 * 1024 * 1024) // 50MB;
                score -= 10;

            // Standart sapma puanlaması (tutarlılık)
            if (metrics.StandardDeviation > metrics.AverageExecutionTime.TotalMilliseconds * 0.5)
                score -= 15;

            return Math.Max(0, score);
        }

        private List<string> GeneratePerformanceRecommendations(FilterPerformanceMetrics metrics)
        {
            var recommendations = new List<string>();

            if (metrics.AverageExecutionTime.TotalMilliseconds > 500)
            {
                recommendations.Add("Consider optimizing filter algorithm for better performance");
            }

            if (metrics.AverageMemoryUsage > 50 * 1024 * 1024)
            {
                recommendations.Add("High memory usage detected. Consider using streaming or chunk processing");
            }

            if (metrics.StandardDeviation > metrics.AverageExecutionTime.TotalMilliseconds * 0.3)
            {
                recommendations.Add("Performance inconsistency detected. Check for external resource dependencies");
            }

            if (!metrics.GpuAccelerationUsed && _gpuManager.IsGpuAvailable())
            {
                recommendations.Add("GPU acceleration is available but not used. Consider enabling for better performance");
            }

            if (recommendations.Count == 0)
            {
                recommendations.Add("Filter performance is optimal");
            }

            return recommendations;
        }

        private async Task<ImageFilter> OptimizeFilterForCachingAsync(ImageFilter filter)
        {
            // Filtre parametrelerini optimize et;
            var optimized = filter.Clone();

            // Varsayılan değerleri normalize et;
            if (optimized.Parameters == null)
            {
                optimized.Parameters = new Dictionary<string, object>();
            }

            // Gereksiz parametreleri temizle;
            var unnecessaryParams = optimized.Parameters;
                .Where(p => p.Value == null ||
                           (p.Value is string str && string.IsNullOrWhiteSpace(str)))
                .Select(p => p.Key)
                .ToList();

            foreach (var param in unnecessaryParams)
            {
                optimized.Parameters.Remove(param);
            }

            // Parametreleri sırala (hash tutarlılığı için)
            optimized.Parameters = optimized.Parameters;
                .OrderBy(p => p.Key)
                .ToDictionary(p => p.Key, p => p.Value);

            await Task.CompletedTask;
            return optimized;
        }

        private async Task<ImageData> ResizeImageAsync(ImageData image, int width, int height)
        {
            // Görüntü ölçeklendirme mantığı;
            // Gerçek implementasyonda System.Drawing veya ImageSharp kullanılır;
            await Task.CompletedTask;

            return new ImageData;
            {
                Width = width,
                Height = height,
                Format = image.Format,
                Data = new byte[width * height * 4] // RGBA formatında;
            };
        }

        private ImageFilter CreateBlurFilter(string name, double radius)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Blur,
                Parameters = new Dictionary<string, object>
                {
                    ["Radius"] = radius,
                    ["KernelSize"] = (int)(radius * 2 + 1)
                },
                Description = $"Gaussian blur with radius {radius}",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateSharpenFilter(string name, double strength)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Sharpen,
                Parameters = new Dictionary<string, object>
                {
                    ["Strength"] = strength,
                    ["KernelSize"] = 3;
                },
                Description = $"Sharpen filter with strength {strength}",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateEdgeDetectionFilter(string name)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.EdgeDetection,
                Parameters = new Dictionary<string, object>
                {
                    ["Algorithm"] = "Sobel",
                    ["Threshold"] = 0.1;
                },
                Description = "Sobel edge detection filter",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateColorCorrectionFilter(string name)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.ColorCorrection,
                Parameters = new Dictionary<string, object>
                {
                    ["AutoWhiteBalance"] = true,
                    ["AutoContrast"] = true;
                },
                Description = "Automatic color correction filter",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateNoiseReductionFilter(string name, int kernelSize)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.NoiseReduction,
                Parameters = new Dictionary<string, object>
                {
                    ["KernelSize"] = kernelSize,
                    ["Method"] = "Median"
                },
                Description = $"Noise reduction filter with kernel size {kernelSize}",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateBrightnessContrastFilter(string name, int brightness, int contrast)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.BrightnessContrast,
                Parameters = new Dictionary<string, object>
                {
                    ["Brightness"] = brightness,
                    ["Contrast"] = contrast;
                },
                Description = $"Brightness {brightness}, Contrast {contrast}",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateSaturationFilter(string name, double saturation)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Saturation,
                Parameters = new Dictionary<string, object>
                {
                    ["Saturation"] = saturation;
                },
                Description = $"Saturation adjustment filter: {saturation}x",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateGrayscaleFilter(string name)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Grayscale,
                Parameters = new Dictionary<string, object>
                {
                    ["Method"] = "Luminosity"
                },
                Description = "Convert to grayscale using luminosity method",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateSepiaFilter(string name)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Sepia,
                Parameters = new Dictionary<string, object>
                {
                    ["Intensity"] = 0.8;
                },
                Description = "Sepia tone filter",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateInvertFilter(string name)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Invert,
                Description = "Invert colors filter",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateEmbossFilter(string name)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Emboss,
                Parameters = new Dictionary<string, object>
                {
                    ["Direction"] = "TopLeft",
                    ["Strength"] = 1.0;
                },
                Description = "Emboss filter",
                Cacheable = true,
                Version = "1.0.0"
            };
        }

        private ImageFilter CreateVignetteFilter(string name, double intensity)
        {
            return new ImageFilter;
            {
                Name = name,
                Type = FilterType.Vignette,
                Parameters = new Dictionary<string, object>
                {
                    ["Intensity"] = intensity,
                    ["Radius"] = 0.75;
                },
                Description = $"Vignette filter with intensity {intensity}",
                Cacheable = true,
                Version = "1.0.0"
            };
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
                    _concurrencySemaphore?.Dispose();
                    _gpuManager?.Dispose();

                    foreach (var processor in _filterProcessors.Values)
                    {
                        if (processor is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }

                    _filterProcessors.Clear();

                    _logger.Info("FilterEngine disposed");
                }

                _disposed = true;
            }
        }

        ~FilterEngine()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Görüntü verisi;
    /// </summary>
    public class ImageData;
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; } // "JPEG", "PNG", "BMP", etc.
        public byte[] Data { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public ColorSpace ColorSpace { get; set; } = ColorSpace.RGB;
        public int BitsPerPixel { get; set; } = 24;
        public bool HasAlpha { get; set; }
        public string FilePath { get; set; }
        public long FileSize { get; set; }
    }

    /// <summary>
    /// Görüntü filtresi;
    /// </summary>
    public class ImageFilter;
    {
        public string Name { get; set; }
        public FilterType Type { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public bool Cacheable { get; set; } = true;
        public bool CacheIntermediateResults { get; set; }
        public FilterFailureAction FailureAction { get; set; } = FilterFailureAction.Continue;
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public ImageFilter Clone()
        {
            return new ImageFilter;
            {
                Name = this.Name,
                Type = this.Type,
                Description = this.Description,
                Parameters = new Dictionary<string, object>(this.Parameters),
                Cacheable = this.Cacheable,
                CacheIntermediateResults = this.CacheIntermediateResults,
                FailureAction = this.FailureAction,
                Version = this.Version,
                Author = this.Author,
                Tags = new List<string>(this.Tags),
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Özel filtre;
    /// </summary>
    public class CustomFilter : ImageFilter;
    {
        public Func<ImageData, Dictionary<string, object>, Task<ImageData>> Implementation { get; set; }
        public bool SupportsGpuAcceleration { get; set; }
        public List<string> RequiredLibraries { get; set; } = new List<string>();
        public string Category { get; set; }
        public Dictionary<string, ParameterInfo> ParameterDefinitions { get; set; } = new Dictionary<string, ParameterInfo>();
    }

    /// <summary>
    /// Parametre bilgisi;
    /// </summary>
    public class ParameterInfo;
    {
        public string Name { get; set; }
        public string Type { get; set; } // "int", "double", "string", "bool", "color"
        public object DefaultValue { get; set; }
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
    }

    /// <summary>
    /// İşlenmiş görüntü;
    /// </summary>
    public class ProcessedImage;
    {
        public Guid OperationId { get; set; }
        public ImageData OriginalImage { get; set; }
        public ImageData FilteredImage { get; set; }
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public FilterMetrics PerformanceMetrics { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Batch filtreleme sonucu;
    /// </summary>
    public class BatchFilterResult;
    {
        public Guid BatchId { get; set; }
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public List<ProcessedImage> ProcessedImages { get; set; } = new List<ProcessedImage>();
        public int TotalImages { get; set; }
        public int SuccessfulImages { get; set; }
        public int FailedImages { get; set; }
        public double SuccessRate { get; set; }
        public bool ParallelProcessing { get; set; }
        public BatchPerformanceMetrics PerformanceMetrics { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Filtre performans metrikleri;
    /// </summary>
    public class FilterPerformanceMetrics;
    {
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public string TestImageSize { get; set; }
        public DateTime TestTimestamp { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan MinExecutionTime { get; set; }
        public TimeSpan MaxExecutionTime { get; set; }
        public double StandardDeviation { get; set; }
        public double AverageMemoryUsage { get; set; } // bytes;
        public double PeakMemoryUsage { get; set; } // bytes;
        public bool GpuAccelerationUsed { get; set; }
        public string ProcessorType { get; set; }
        public int PerformanceScore { get; set; } // 0-100;
        public List<string> Recommendations { get; set; } = new List<string>();
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Filtre türleri;
    /// </summary>
    public enum FilterType;
    {
        Blur = 0,
        Sharpen = 1,
        EdgeDetection = 2,
        ColorCorrection = 3,
        NoiseReduction = 4,
        BrightnessContrast = 5,
        Saturation = 6,
        HueRotate = 7,
        Sepia = 8,
        Grayscale = 9,
        Invert = 10,
        Threshold = 11,
        Emboss = 12,
        GaussianBlur = 13,
        MotionBlur = 14,
        ZoomBlur = 15,
        OilPainting = 16,
        Watercolor = 17,
        Cartoon = 18,
        Vignette = 19,
        Custom = 100,
        CustomPipeline = 101;
    }

    /// <summary>
    /// Renk uzayı;
    /// </summary>
    public enum ColorSpace;
    {
        RGB = 0,
        RGBA = 1,
        ARGB = 2,
        BGR = 3,
        BGRA = 4,
        CMYK = 5,
        Grayscale = 6,
        HSL = 7,
        HSV = 8,
        YCbCr = 9,
        Lab = 10;
    }

    /// <summary>
    /// Filtre başarısızlık eylemi;
    /// </summary>
    public enum FilterFailureAction;
    {
        Continue = 0,
        StopPipeline = 1,
        UseFallback = 2,
        Retry = 3;
    }

    /// <summary>
    /// Filtre bilgisi;
    /// </summary>
    public class FilterInfo;
    {
        public string Name { get; set; }
        public FilterType Type { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Author { get; set; }
        public string Version { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, ParameterInfo> Parameters { get; set; } = new Dictionary<string, ParameterInfo>();
        public FilterPerformanceInfo PerformanceMetrics { get; set; }
        public bool SupportsGpuAcceleration { get; set; }
        public bool IsCustom { get; set; }
        public DateTime RegisteredDate { get; set; }
    }

    /// <summary>
    /// Filtre performans bilgisi;
    /// </summary>
    public class FilterPerformanceInfo;
    {
        public string FilterName { get; set; }
        public int TotalExecutions { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public TimeSpan AverageExecutionTime { get; set; }
        public TimeSpan MinExecutionTime { get; set; }
        public TimeSpan MaxExecutionTime { get; set; }
        public double TotalMemoryUsage { get; set; } // bytes;
        public double AverageMemoryUsage { get; set; } // bytes;
        public int GpuExecutions { get; set; }
        public DateTime LastExecution { get; set; }
        public Dictionary<string, object> Statistics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Uygulanan filtre bilgisi;
    /// </summary>
    public class AppliedFilterInfo;
    {
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public FilterMetrics PerformanceMetrics { get; set; }
    }

    /// <summary>
    /// Pipeline metrikleri;
    /// </summary>
    public class PipelineMetrics;
    {
        public int TotalSteps { get; set; }
        public int SuccessfulSteps { get; set; }
        public int FailedSteps { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public double AverageStepTime { get; set; }
        public Dictionary<string, object> StepMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Batch metrikleri;
    /// </summary>
    public class BatchMetrics;
    {
        public int ProcessedImages { get; set; }
        public int SuccessfulImages { get; set; }
        public int FailedImages { get; set; }
    }

    /// <summary>
    /// Batch performans metrikleri;
    /// </summary>
    public class BatchPerformanceMetrics;
    {
        public TimeSpan AverageProcessingTime { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public TimeSpan MinProcessingTime { get; set; }
        public TimeSpan MaxProcessingTime { get; set; }
        public double AverageMemoryUsage { get; set; } // bytes;
        public double PeakMemoryUsage { get; set; } // bytes;
        public int GpuAcceleratedOperations { get; set; }
        public double OperationsPerSecond { get; set; }
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    #endregion;

    #region Event Classes;

    /// <summary>
    /// Filtre uygulandı olayı;
    /// </summary>
    public class FilterAppliedEvent : IEvent;
    {
        public Guid OperationId { get; set; }
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public string ImageSize { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Filtre pipeline'ı tamamlandı olayı;
    /// </summary>
    public class FilterPipelineCompletedEvent : IEvent;
    {
        public Guid PipelineId { get; set; }
        public int TotalFilters { get; set; }
        public int SuccessfulSteps { get; set; }
        public int FailedSteps { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Batch filtreleme tamamlandı olayı;
    /// </summary>
    public class BatchFilterCompletedEvent : IEvent;
    {
        public Guid BatchId { get; set; }
        public string FilterName { get; set; }
        public int TotalImages { get; set; }
        public int SuccessfulImages { get; set; }
        public int FailedImages { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Filtre önbelleğe alındı olayı;
    /// </summary>
    public class FilterCachedEvent : IEvent;
    {
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public DateTime CacheTimestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Özel filtre kaydedildi olayı;
    /// </summary>
    public class CustomFilterRegisteredEvent : IEvent;
    {
        public string FilterName { get; set; }
        public FilterType FilterType { get; set; }
        public string Author { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Filtre işlemi iptal edildi olayı;
    /// </summary>
    public class FilterOperationCancelledEvent : IEvent;
    {
        public Guid OperationId { get; set; }
        public string FilterName { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Filtre işlemi başarısız oldu olayı;
    /// </summary>
    public class FilterOperationFailedEvent : IEvent;
    {
        public Guid OperationId { get; set; }
        public string FilterName { get; set; }
        public string ErrorMessage { get; set; }
        public string ExceptionType { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    #endregion;

    #region Exception Classes;

    /// <summary>
    /// Filtre motoru istisnası;
    /// </summary>
    public class FilterEngineException : Exception
    {
        public FilterEngineException(string message) : base(message) { }
        public FilterEngineException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Filtre işleme istisnası;
    /// </summary>
    public class FilterProcessingException : Exception
    {
        public FilterProcessingException(string message) : base(message) { }
        public FilterProcessingException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Pipeline yürütme istisnası;
    /// </summary>
    public class PipelineExecutionException : Exception
    {
        public PipelineExecutionException(string message) : base(message) { }
        public PipelineExecutionException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Filtre işlemcisi bulunamadı istisnası;
    /// </summary>
    public class FilterProcessorNotFoundException : Exception
    {
        public FilterProcessorNotFoundException(string message) : base(message) { }
        public FilterProcessorNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// GPU hızlandırma istisnası;
    /// </summary>
    public class GpuAccelerationException : Exception
    {
        public GpuAccelerationException(string message) : base(message) { }
        public GpuAccelerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;

    #region Internal Helper Classes and Interfaces;

    internal interface IFilterProcessor;
    {
        FilterType SupportedFilterType { get; }
        bool SupportsGpuAcceleration { get; }
        Task<FilterResult> ProcessAsync(FilterContext context);
        Task SetGpuAccelerationAsync(bool enable);
        Task PreloadAsync(ImageFilter filter);
    }

    internal class FilterContext;
    {
        public ImageData Image { get; set; }
        public ImageFilter Filter { get; set; }
        public Guid OperationId { get; set; }
        public bool UseGpuAcceleration { get; set; }
        public CancellationToken CancellationToken { get; set; }
        public Dictionary<string, object> AdditionalContext { get; set; } = new Dictionary<string, object>();
    }

    internal class FilterResult;
    {
        public ImageData ProcessedImage { get; set; }
        public bool Success { get; set; }
        public string ErrorMessage { get; set; }
        public FilterMetrics Metrics { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; } = new Dictionary<string, object>();
    }

    internal class FilterMetrics;
    {
        public TimeSpan ProcessingTime { get; set; }
        public long MemoryUsage { get; set; } // bytes;
        public bool GpuAccelerationUsed { get; set; }
        public double GpuUtilization { get; set; } // percentage;
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    internal class FilterCache;
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, ProcessedImage> _cache = new ConcurrentDictionary<string, ProcessedImage>();
        private readonly ConcurrentDictionary<string, ImageFilter> _filterCache = new ConcurrentDictionary<string, ImageFilter>();
        private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(1);
        private DateTime _lastCleanup = DateTime.UtcNow;

        public FilterCache(ILogger logger)
        {
            _logger = logger;
        }

        public bool TryGet(string key, out ProcessedImage result)
        {
            CleanupIfNeeded();

            if (_cache.TryGetValue(key, out result))
            {
                if (DateTime.UtcNow - GetCacheTimestamp(result) < _cacheDuration)
                {
                    return true;
                }
                _cache.TryRemove(key, out _);
            }

            result = null;
            return false;
        }

        public void Add(string key, ProcessedImage result)
        {
            // Cache boyutunu kontrol et;
            if (_cache.Count >= 1000)
            {
                CleanupOldest(500);
            }

            _cache[key] = result;
            _logger.Debug($"Added to filter cache: {key}");
        }

        public void CacheFilter(ImageFilter filter)
        {
            var key = ComputeFilterHash(filter);
            _filterCache[key] = filter;
            _logger.Debug($"Cached filter: {filter.Name}");
        }

        public bool TryGetFilter(string key, out ImageFilter filter)
        {
            return _filterCache.TryGetValue(key, out filter);
        }

        private DateTime GetCacheTimestamp(ProcessedImage result)
        {
            if (result.Metadata.TryGetValue("CacheTimestamp", out var timestamp) &&
                timestamp is DateTime cachedTime)
            {
                return cachedTime;
            }
            return DateTime.UtcNow.AddHours(-2); // Varsayılan: 2 saat önce;
        }

        private void CleanupIfNeeded()
        {
            if (DateTime.UtcNow - _lastCleanup > TimeSpan.FromMinutes(5))
            {
                var expiredKeys = _cache;
                    .Where(kvp => DateTime.UtcNow - GetCacheTimestamp(kvp.Value) > _cacheDuration)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in expiredKeys)
                {
                    _cache.TryRemove(key, out _);
                }

                _lastCleanup = DateTime.UtcNow;
                _logger.Debug($"Filter cache cleaned up. Removed {expiredKeys.Count} expired entries");
            }
        }

        private void CleanupOldest(int countToKeep)
        {
            var itemsToRemove = _cache;
                .OrderBy(kvp => GetCacheTimestamp(kvp.Value))
                .Take(_cache.Count - countToKeep)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in itemsToRemove)
            {
                _cache.TryRemove(key, out _);
            }

            _logger.Debug($"Filter cache trimmed. Removed {itemsToRemove.Count} oldest entries");
        }

        private string ComputeFilterHash(ImageFilter filter)
        {
            var data = $"{filter.Name}_{filter.Type}_{filter.Version}";
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(data));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    internal class FilterRegistry
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<string, FilterInfo> _filters = new ConcurrentDictionary<string, FilterInfo>();

        public FilterRegistry(ILogger logger)
        {
            _logger = logger;
        }

        public async Task RegisterFilterAsync(ImageFilter filter)
        {
            var filterInfo = new FilterInfo;
            {
                Name = filter.Name,
                Type = filter.Type,
                Description = filter.Description,
                Author = filter.Author,
                Version = filter.Version,
                Tags = new List<string>(filter.Tags),
                RegisteredDate = DateTime.UtcNow,
                IsCustom = filter is CustomFilter,
                SupportsGpuAcceleration = (filter as CustomFilter)?.SupportsGpuAcceleration ?? false;
            };

            _filters[filter.Name] = filterInfo;
            _logger.Debug($"Registered filter: {filter.Name}");
            await Task.CompletedTask;
        }

        public async Task<IEnumerable<FilterInfo>> GetAllFiltersAsync()
        {
            await Task.CompletedTask;
            return _filters.Values.OrderBy(f => f.Name).ToList();
        }

        public async Task<FilterInfo> GetFilterAsync(string name)
        {
            if (_filters.TryGetValue(name, out var filter))
            {
                await Task.CompletedTask;
                return filter;
            }

            throw new KeyNotFoundException($"Filter not found: {name}");
        }
    }

    internal class GpuAccelerationManager : IDisposable
    {
        private readonly ILogger _logger;
        private bool _gpuEnabled;
        private bool _disposed;

        public GpuAccelerationManager(ILogger logger)
        {
            _logger = logger;
            _gpuEnabled = DetectGpuAvailability();
        }

        public bool IsGpuAvailable()
        {
            // GPU varlığını tespit et;
            return DetectGpuAvailability();
        }

        public async Task SetGpuAccelerationAsync(bool enable)
        {
            if (enable && !IsGpuAvailable())
            {
                throw new GpuAccelerationException("GPU is not available on this system");
            }

            _gpuEnabled = enable;
            _logger.Info($"GPU acceleration {(enable ? "enabled" : "disabled")}");
            await Task.CompletedTask;
        }

        public async Task AcquireGpuContextAsync()
        {
            if (!_gpuEnabled)
                return;

            // GPU context edinme mantığı;
            await Task.CompletedTask;
        }

        public void ReleaseGpuContext()
        {
            if (!_gpuEnabled)
                return;

            // GPU context serbest bırakma;
        }

        private bool DetectGpuAvailability()
        {
            try
            {
                // GPU varlığını tespit etme mantığı;
                // Burada DirectX, CUDA, OpenCL kontrolleri yapılabilir;
                return false; // Varsayılan: GPU yok;
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                ReleaseGpuContext();
                _disposed = true;
            }
        }
    }

    internal class PipelineOptimizer;
    {
        private readonly ILogger _logger;

        public PipelineOptimizer(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<List<ImageFilter>> OptimizePipelineAsync(List<ImageFilter> filters)
        {
            var optimized = new List<ImageFilter>();

            foreach (var filter in filters)
            {
                // Pipeline optimizasyonu mantığı;
                // 1. Gereksiz filtreleri kaldır;
                // 2. Benzer filtreleri birleştir;
                // 3. Sıralamayı optimize et;

                if (!ShouldRemoveFilter(filter, optimized))
                {
                    optimized.Add(filter);
                }
            }

            await Task.CompletedTask;
            return optimized;
        }

        private bool ShouldRemoveFilter(ImageFilter filter, List<ImageFilter> currentPipeline)
        {
            // Optimizasyon kuralları;
            // Örnek: Arka arkaya iki blur filtresi varsa, sadece birini tut;

            if (currentPipeline.Count == 0)
                return false;

            var lastFilter = currentPipeline.Last();

            // Aynı tip filtrelerden art arda gelenleri birleştir;
            if (lastFilter.Type == filter.Type &&
                lastFilter.Type == FilterType.Blur)
            {
                _logger.Debug($"Merging consecutive {filter.Type} filters");
                return true;
            }

            // Birbirini iptal eden filtreler;
            if ((lastFilter.Type == FilterType.BrightnessContrast && filter.Type == FilterType.BrightnessContrast) ||
                (lastFilter.Type == FilterType.Saturation && filter.Type == FilterType.Saturation))
            {
                // Parametre analizi yaparak iptal edip etmediğine karar ver;
                return AreFiltersCancelling(lastFilter, filter);
            }

            return false;
        }

        private bool AreFiltersCancelling(ImageFilter filter1, ImageFilter filter2)
        {
            // Filtrelerin birbirini iptal edip etmediğini kontrol et;
            // Örnek: Brightness +10 ve sonra -10;
            return false;
        }
    }

    internal class CustomFilterProcessor : IFilterProcessor;
    {
        private readonly CustomFilter _filter;
        private readonly ILogger _logger;
        private bool _gpuAcceleration;

        public FilterType SupportedFilterType => _filter.Type;
        public bool SupportsGpuAcceleration => _filter.SupportsGpuAcceleration;

        public CustomFilterProcessor(CustomFilter filter, ILogger logger)
        {
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<FilterResult> ProcessAsync(FilterContext context)
        {
            var startTime = DateTime.UtcNow;
            var initialMemory = GC.GetTotalMemory(false);

            try
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                _logger.Debug($"Processing custom filter: {_filter.Name}");

                // Özel filtreyi uygula;
                var processedImage = await _filter.Implementation(context.Image, context.Filter.Parameters);

                var endTime = DateTime.UtcNow;
                var finalMemory = GC.GetTotalMemory(false);

                return new FilterResult;
                {
                    ProcessedImage = processedImage,
                    Success = true,
                    Metrics = new FilterMetrics;
                    {
                        ProcessingTime = endTime - startTime,
                        MemoryUsage = finalMemory - initialMemory,
                        GpuAccelerationUsed = _gpuAcceleration && SupportsGpuAcceleration;
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.Error($"Custom filter processing failed: {ex.Message}", ex);

                return new FilterResult;
                {
                    Success = false,
                    ErrorMessage = $"Custom filter failed: {ex.Message}",
                    Metrics = new FilterMetrics;
                    {
                        ProcessingTime = DateTime.UtcNow - startTime;
                    }
                };
            }
        }

        public async Task SetGpuAccelerationAsync(bool enable)
        {
            _gpuAcceleration = enable && SupportsGpuAcceleration;
            await Task.CompletedTask;
        }

        public async Task PreloadAsync(ImageFilter filter)
        {
            // Özel filtre için ön yükleme;
            await Task.CompletedTask;
        }
    }

    // Yerleşik filtre işlemcileri (örnek implementasyonlar)

    internal class BlurFilterProcessor : IFilterProcessor;
    {
        private readonly ILogger _logger;
        private bool _gpuAcceleration;

        public FilterType SupportedFilterType => FilterType.Blur;
        public bool SupportsGpuAcceleration => true;

        public BlurFilterProcessor(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<FilterResult> ProcessAsync(FilterContext context)
        {
            // Blur filtre implementasyonu;
            await Task.CompletedTask;
            return new FilterResult();
        }

        public async Task SetGpuAccelerationAsync(bool enable)
        {
            _gpuAcceleration = enable;
            await Task.CompletedTask;
        }

        public async Task PreloadAsync(ImageFilter filter)
        {
            await Task.CompletedTask;
        }
    }

    internal class SharpenFilterProcessor : IFilterProcessor;
    {
        private readonly ILogger _logger;
        private bool _gpuAcceleration;

        public FilterType SupportedFilterType => FilterType.Sharpen;
        public bool SupportsGpuAcceleration => true;

        public SharpenFilterProcessor(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<FilterResult> ProcessAsync(FilterContext context)
        {
            // Sharpen filtre implementasyonu;
            await Task.CompletedTask;
            return new FilterResult();
        }

        public async Task SetGpuAccelerationAsync(bool enable)
        {
            _gpuAcceleration = enable;
            await Task.CompletedTask;
        }

        public async Task PreloadAsync(ImageFilter filter)
        {
            await Task.CompletedTask;
        }
    }

    // Diğer filtre işlemcileri için benzer implementasyonlar...

    internal class EdgeDetectionFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class ColorCorrectionFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class NoiseReductionFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class BrightnessContrastFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class SaturationFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class HueRotateFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class SepiaFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class GrayscaleFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class InvertFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class ThresholdFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class EmbossFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class GaussianBlurFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class MotionBlurFilterProcessor : IFilterProcessor { /* Implementación */ }
    internal class ZoomBlurFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class OilPaintingFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class WatercolorFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class CartoonFilterProcessor : IFilterProcessor { /* Implementasyon */ }
    internal class VignetteFilterProcessor : IFilterProcessor { /* Implementasyon */ }

    #endregion;
}
