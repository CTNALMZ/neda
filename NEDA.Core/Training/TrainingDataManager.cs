using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.Core.Logging;
using NEDA.Core.Configuration.AppSettings;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Drawing;
using System.Numerics;

namespace NEDA.Core.Training
{
    /// <summary>
    /// Eğitim verilerinin yönetimi, işlenmesi, augmentasyonu ve depolanmasından sorumlu servis.
    /// </summary>
    public class TrainingDataManager : ITrainingDataManager, IDisposable
    {
        private readonly ILogger<TrainingDataManager> _logger;
        private readonly IAuditLogger _auditLogger;
        private readonly TrainingDataConfig _config;
        private readonly IDataAugmentationService _augmentationService;
        private readonly IDataValidationService _validationService;
        private readonly IDataTransformationService _transformationService;

        // Data storage
        private readonly ConcurrentDictionary<string, Dataset> _datasets;
        private readonly ConcurrentDictionary<string, DataBatch> _activeBatches;
        private readonly ConcurrentDictionary<string, DataPipeline> _pipelines;

        // Cache
        private readonly MemoryCache<string, CachedDataset> _datasetCache;
        private readonly MemoryCache<string, DataStatistics> _statisticsCache;

        // File system
        private readonly string _baseDataPath;
        private readonly string _tempPath;

        // Background tasks
        private readonly CancellationTokenSource _backgroundCts;
        private Task _cleanupTask;
        private Task _indexingTask;

        // Performance tracking
        private readonly DataManagerMetrics _metrics;
        private readonly object _syncLock = new object();

        // Data processing
        private readonly SemaphoreSlim _processingSemaphore;
        private readonly ConcurrentQueue<DataProcessingJob> _processingQueue;

        // Data sources
        private readonly List<IDataSource> _dataSources;
        private readonly Dictionary<string, IDataAdapter> _dataAdapters;

        /// <summary>
        /// TrainingDataManager constructor
        /// </summary>
        public TrainingDataManager(
            ILogger<TrainingDataManager> logger,
            IAuditLogger auditLogger,
            IOptions<TrainingDataConfig> config,
            IDataAugmentationService augmentationService,
            IDataValidationService validationService,
            IDataTransformationService transformationService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _augmentationService = augmentationService ?? throw new ArgumentNullException(nameof(augmentationService));
            _validationService = validationService ?? throw new ArgumentNullException(nameof(validationService));
            _transformationService = transformationService ?? throw new ArgumentNullException(nameof(transformationService));

            _datasets = new ConcurrentDictionary<string, Dataset>();
            _activeBatches = new ConcurrentDictionary<string, DataBatch>();
            _pipelines = new ConcurrentDictionary<string, DataPipeline>();

            _datasetCache = new MemoryCache<string, CachedDataset>(_config.MaxCacheSize);
            _statisticsCache = new MemoryCache<string, DataStatistics>(_config.MaxStatisticsCache);

            _baseDataPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _config.BaseDataPath);
            _tempPath = Path.Combine(_baseDataPath, "temp");

            _backgroundCts = new CancellationTokenSource();

            _metrics = new DataManagerMetrics();

            _processingSemaphore = new SemaphoreSlim(_config.MaxConcurrentProcessings, _config.MaxConcurrentProcessings);
            _processingQueue = new ConcurrentQueue<DataProcessingJob>();

            _dataSources = new List<IDataSource>();
            _dataAdapters = new Dictionary<string, IDataAdapter>();

            Initialize();
        }

        /// <summary>
        /// Data manager'ı başlatır
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                // Create directories
                EnsureDirectories();

                // Load existing datasets
                await LoadExistingDatasetsAsync();

                // Initialize data sources
                await InitializeDataSourcesAsync();

                // Start background tasks
                StartBackgroundTasks();

                // Start processing queue
                StartProcessingQueue();

                _metrics.Status = DataManagerStatus.Running;
                _metrics.StartTime = DateTime.UtcNow;

                await _auditLogger.LogSystemEventAsync(
                    "TRAINING_DATA_MANAGER_STARTED",
                    "Training data manager started successfully",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Training data manager started successfully");
            }
            catch (Exception ex)
            {
                _metrics.Status = DataManagerStatus.Error;
                _logger.LogError(ex, "Failed to initialize training data manager");
                throw new TrainingDataException(ErrorCodes.DATA_MANAGER_INIT_FAILED,
                    "Failed to initialize training data manager", ex);
            }
        }

        /// <summary>
        /// Yeni dataset oluşturur
        /// </summary>
        public async Task<Dataset> CreateDatasetAsync(CreateDatasetRequest request)
        {
            ValidateCreateDatasetRequest(request);

            var datasetId = GenerateDatasetId(request.Name);

            try
            {
                // Check if dataset already exists
                if (_datasets.ContainsKey(datasetId))
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_ALREADY_EXISTS,
                        $"Dataset already exists: {datasetId}");
                }

                // Create dataset
                var dataset = new Dataset
                {
                    Id = datasetId,
                    Name = request.Name,
                    Description = request.Description,
                    DataType = request.DataType,
                    Domain = request.Domain,
                    Format = request.Format,
                    Version = 1,
                    Status = DatasetStatus.Creating,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Metadata = request.Metadata ?? new Dictionary<string, object>(),
                    Statistics = new DataStatistics(),
                    StorageInfo = new StorageInfo
                    {
                        Path = Path.Combine(_baseDataPath, datasetId),
                        Compression = request.Compression,
                        Encryption = request.Encryption,
                        RetentionDays = request.RetentionDays ?? _config.DefaultRetentionDays
                    }
                };

                // Create storage directory
                EnsureDatasetDirectory(dataset.StorageInfo.Path);

                // Process data if provided
                if (request.Data != null || !string.IsNullOrEmpty(request.DataSource))
                {
                    await ProcessAndStoreDataAsync(dataset, request);
                }

                // Add to collections
                _datasets[datasetId] = dataset;

                // Cache dataset info
                await CacheDatasetAsync(dataset);

                // Update metrics
                _metrics.DatasetsCreated++;

                await _auditLogger.LogSystemEventAsync(
                    "DATASET_CREATED",
                    $"Dataset created: {dataset.Name} (ID: {datasetId})",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Dataset created: {Name} (ID: {Id}, Type: {Type})",
                    dataset.Name, dataset.Id, dataset.DataType);

                return dataset;
            }
            catch (TrainingDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create dataset: {Name}", request.Name);
                throw new TrainingDataException(ErrorCodes.DATASET_CREATION_FAILED,
                    $"Failed to create dataset: {request.Name}", ex);
            }
        }

        /// <summary>
        /// Datasete veri ekler
        /// </summary>
        public async Task<bool> AddDataToDatasetAsync(string datasetId, AddDataRequest request)
        {
            ValidateAddDataRequest(datasetId, request);

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(datasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {datasetId}");
                }

                // Check dataset status
                if (dataset.Status != DatasetStatus.Active && dataset.Status != DatasetStatus.Partial)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_READY,
                        $"Dataset not ready for adding data: {dataset.Status}");
                }

                // Process and add data
                var addedCount = await ProcessAndAddDataAsync(dataset, request);

                // Update dataset
                dataset.UpdatedAt = DateTime.UtcNow;
                dataset.TotalItems += addedCount;
                dataset.Status = dataset.TotalItems >= request.ExpectedCount
                    ? DatasetStatus.Active
                    : DatasetStatus.Partial;

                // Update statistics
                await UpdateDatasetStatisticsAsync(dataset);

                // Clear cache
                _datasetCache.Remove(datasetId);
                _statisticsCache.Remove(datasetId);

                // Update metrics
                _metrics.DataItemsAdded += addedCount;

                await _auditLogger.LogSystemEventAsync(
                    "DATA_ADDED_TO_DATASET",
                    $"Added {addedCount} items to dataset: {dataset.Name}",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Added {Count} items to dataset: {DatasetName}",
                    addedCount, dataset.Name);

                return addedCount > 0;
            }
            catch (TrainingDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add data to dataset {DatasetId}", datasetId);
                throw new TrainingDataException(ErrorCodes.DATA_ADDITION_FAILED,
                    $"Failed to add data to dataset {datasetId}", ex);
            }
        }

        /// <summary>
        /// Dataset'ten veri okur
        /// </summary>
        public async Task<DataBatch> GetDataBatchAsync(string datasetId, BatchRequest request)
        {
            ValidateBatchRequest(datasetId, request);

            var batchId = Guid.NewGuid().ToString();

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(datasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {datasetId}");
                }

                // Check if dataset is ready
                if (dataset.Status != DatasetStatus.Active)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_READY,
                        $"Dataset not ready for reading: {dataset.Status}");
                }

                // Create batch
                var batch = new DataBatch
                {
                    Id = batchId,
                    DatasetId = datasetId,
                    DatasetVersion = dataset.Version,
                    Request = request,
                    Status = BatchStatus.Preparing,
                    CreatedAt = DateTime.UtcNow
                };

                // Add to active batches
                _activeBatches[batchId] = batch;

                // Load data
                await LoadBatchDataAsync(batch, dataset, request);

                // Apply transformations if requested
                if (request.Transformations != null && request.Transformations.Any())
                {
                    await ApplyTransformationsAsync(batch, request.Transformations);
                }

                // Apply augmentation if requested
                if (request.Augmentations != null && request.Augmentations.Any())
                {
                    await ApplyAugmentationsAsync(batch, request.Augmentations);
                }

                // Update batch status
                batch.Status = BatchStatus.Ready;
                batch.PreparedAt = DateTime.UtcNow;

                // Update metrics
                _metrics.BatchesServed++;
                _metrics.TotalItemsServed += batch.Items.Count;

                return batch;
            }
            catch (TrainingDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get data batch from dataset {DatasetId}", datasetId);

                if (_activeBatches.TryGetValue(batchId, out var batch))
                {
                    batch.Status = BatchStatus.Failed;
                    batch.Error = ex.Message;
                }

                throw new TrainingDataException(ErrorCodes.BATCH_LOAD_FAILED,
                    $"Failed to get data batch from dataset {datasetId}", ex);
            }
        }

        /// <summary>
        /// Dataset istatistiklerini getirir
        /// </summary>
        public async Task<DataStatistics> GetDatasetStatisticsAsync(string datasetId)
        {
            if (string.IsNullOrWhiteSpace(datasetId))
                throw new ArgumentException("Dataset ID cannot be null or empty", nameof(datasetId));

            try
            {
                // Check cache first
                if (_statisticsCache.TryGet(datasetId, out var cachedStats))
                {
                    return cachedStats;
                }

                // Get dataset
                var dataset = await GetDatasetAsync(datasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {datasetId}");
                }

                // Calculate statistics if not available
                if (dataset.Statistics == null || dataset.Statistics.TotalItems == 0)
                {
                    await UpdateDatasetStatisticsAsync(dataset);
                }

                // Cache statistics
                _statisticsCache.Set(datasetId, dataset.Statistics, TimeSpan.FromMinutes(_config.StatisticsCacheMinutes));

                return dataset.Statistics;
            }
            catch (TrainingDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get statistics for dataset {DatasetId}", datasetId);
                throw new TrainingDataException(ErrorCodes.STATISTICS_RETRIEVAL_FAILED,
                    $"Failed to get statistics for dataset {datasetId}", ex);
            }
        }

        /// <summary>
        /// Dataset'i böler (train/validation/test)
        /// </summary>
        public async Task<DatasetSplit> SplitDatasetAsync(string datasetId, SplitRequest request)
        {
            ValidateSplitRequest(datasetId, request);

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(datasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {datasetId}");
                }

                // Check if dataset is large enough for split
                if (dataset.TotalItems < request.MinimumSize)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_TOO_SMALL,
                        $"Dataset too small for split: {dataset.TotalItems} items");
                }

                // Create split
                var split = new DatasetSplit
                {
                    OriginalDatasetId = datasetId,
                    Request = request,
                    CreatedAt = DateTime.UtcNow
                };

                // Perform split
                await PerformDatasetSplitAsync(split, dataset, request);

                // Create split datasets
                await CreateSplitDatasetsAsync(split, dataset);

                // Update metrics
                _metrics.DatasetSplits++;

                await _auditLogger.LogSystemEventAsync(
                    "DATASET_SPLIT_CREATED",
                    $"Dataset split created for {dataset.Name}: {split.TrainDatasetId}, {split.ValidationDatasetId}, {split.TestDatasetId}",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Dataset split created for {DatasetName}", dataset.Name);

                return split;
            }
            catch (TrainingDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to split dataset {DatasetId}", datasetId);
                throw new TrainingDataException(ErrorCodes.DATASET_SPLIT_FAILED,
                    $"Failed to split dataset {datasetId}", ex);
            }
        }

        /// <summary>
        /// Data augmentation uygular
        /// </summary>
        public async Task<AugmentationResult> AugmentDataAsync(AugmentationRequest request)
        {
            ValidateAugmentationRequest(request);

            try
            {
                var result = new AugmentationResult
                {
                    Request = request,
                    StartedAt = DateTime.UtcNow
                };

                // Get source data
                var sourceData = await GetDataForAugmentationAsync(request);

                // Apply augmentations
                var augmentedData = await _augmentationService.AugmentAsync(sourceData, request.Augmentations);

                // Store augmented data
                result.AugmentedDatasetId = await StoreAugmentedDataAsync(augmentedData, request);
                result.AugmentedCount = augmentedData.Count;
                result.CompletedAt = DateTime.UtcNow;

                // Update metrics
                _metrics.DataAugmentations++;
                _metrics.TotalAugmentedItems += augmentedData.Count;

                await _auditLogger.LogSystemEventAsync(
                    "DATA_AUGMENTATION_COMPLETED",
                    $"Data augmentation completed: {augmentedData.Count} items created",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Data augmentation completed: {Count} items created", augmentedData.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to augment data");
                throw new TrainingDataException(ErrorCodes.AUGMENTATION_FAILED,
                    "Failed to augment data", ex);
            }
        }

        /// <summary>
        /// Dataset'i dönüştürür
        /// </summary>
        public async Task<TransformationResult> TransformDatasetAsync(TransformationRequest request)
        {
            ValidateTransformationRequest(request);

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(request.DatasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {request.DatasetId}");
                }

                // Create transformation job
                var job = new DataProcessingJob
                {
                    Id = Guid.NewGuid().ToString(),
                    Type = ProcessingJobType.Transformation,
                    DatasetId = request.DatasetId,
                    Request = request,
                    Status = ProcessingJobStatus.Pending,
                    CreatedAt = DateTime.UtcNow
                };

                // Enqueue job
                _processingQueue.Enqueue(job);

                // Wait for completion
                var result = await WaitForJobCompletionAsync(job.Id);

                return new TransformationResult
                {
                    OriginalDatasetId = request.DatasetId,
                    TransformedDatasetId = result.OutputDatasetId,
                    TransformationsApplied = request.Transformations.Count,
                    ItemsProcessed = result.ItemsProcessed
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to transform dataset {DatasetId}", request.DatasetId);
                throw new TrainingDataException(ErrorCodes.TRANSFORMATION_FAILED,
                    $"Failed to transform dataset {request.DatasetId}", ex);
            }
        }

        /// <summary>
        /// Dataset'i temizler
        /// </summary>
        public async Task<CleaningResult> CleanDatasetAsync(CleaningRequest request)
        {
            ValidateCleaningRequest(request);

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(request.DatasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {request.DatasetId}");
                }

                var result = new CleaningResult
                {
                    OriginalDatasetId = request.DatasetId,
                    StartedAt = DateTime.UtcNow,
                    CleaningRules = request.CleaningRules
                };

                // Load data for cleaning
                var data = await LoadDatasetDataAsync(dataset, new BatchRequest
                {
                    BatchSize = (int)Math.Min(int.MaxValue, dataset.TotalItems),
                    Shuffle = false
                });

                // Apply cleaning rules
                var (cleanedData, removedItems) = await _validationService.CleanDataAsync(data, request.CleaningRules);

                // Store cleaned data
                if (request.CreateNewDataset)
                {
                    result.CleanedDatasetId = await CreateCleanedDatasetAsync(dataset, cleanedData, request);
                }
                else
                {
                    await ReplaceDatasetDataAsync(dataset, cleanedData);
                }

                // Update result
                result.CompletedAt = DateTime.UtcNow;
                result.OriginalItemCount = data.Count;
                result.CleanedItemCount = cleanedData.Count;
                result.RemovedItemCount = removedItems;
                result.RemovedReasons = await AnalyzeRemovedItemsAsync(data, cleanedData);

                // Update metrics
                _metrics.DatasetsCleaned++;
                _metrics.TotalItemsCleaned += cleanedData.Count;

                await _auditLogger.LogSystemEventAsync(
                    "DATASET_CLEANING_COMPLETED",
                    $"Dataset cleaning completed: {removedItems} items removed",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Dataset cleaning completed: {Removed} items removed, {Remaining} items remaining",
                    removedItems, cleanedData.Count);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clean dataset {DatasetId}", request.DatasetId);
                throw new TrainingDataException(ErrorCodes.CLEANING_FAILED,
                    $"Failed to clean dataset {request.DatasetId}", ex);
            }
        }

        /// <summary>
        /// Dataset'i dışa aktarır
        /// </summary>
        public async Task<ExportResult> ExportDatasetAsync(ExportRequest request)
        {
            ValidateExportRequest(request);

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(request.DatasetId);
                if (dataset == null)
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_NOT_FOUND,
                        $"Dataset not found: {request.DatasetId}");
                }

                var result = new ExportResult
                {
                    DatasetId = request.DatasetId,
                    Format = request.Format,
                    StartedAt = DateTime.UtcNow
                };

                // Load data
                var data = await LoadDatasetDataAsync(dataset, new BatchRequest
                {
                    BatchSize = request.MaxItems ?? (int)Math.Min(int.MaxValue, dataset.TotalItems),
                    Shuffle = request.Shuffle
                });

                // Apply filtering if specified
                if (request.Filters != null && request.Filters.Any())
                {
                    data = await ApplyFiltersAsync(data, request.Filters);
                }

                // Transform to target format
                var exportData = await TransformToExportFormatAsync(data, request.Format, request.Options);

                // Write to output
                result.FilePath = await WriteExportFileAsync(exportData, request);
                result.ItemCount = data.Count;
                result.CompletedAt = DateTime.UtcNow;
                result.FileSize = new FileInfo(result.FilePath).Length;

                // Update metrics
                _metrics.DatasetExports++;

                await _auditLogger.LogSystemEventAsync(
                    "DATASET_EXPORTED",
                    $"Dataset exported: {dataset.Name} to {request.Format} format",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Dataset exported: {DatasetName} ({Count} items) to {Format}",
                    dataset.Name, data.Count, request.Format);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to export dataset {DatasetId}", request.DatasetId);
                throw new TrainingDataException(ErrorCodes.EXPORT_FAILED,
                    $"Failed to export dataset {request.DatasetId}", ex);
            }
        }

        /// <summary>
        /// Dış kaynaktan veri içe aktarır
        /// </summary>
        public async Task<ImportResult> ImportDataAsync(ImportRequest request)
        {
            ValidateImportRequest(request);

            try
            {
                var result = new ImportResult
                {
                    Source = request.Source,
                    Format = request.Format,
                    StartedAt = DateTime.UtcNow
                };

                // Read data from source
                var sourceData = await ReadImportSourceAsync(request);

                // Transform data
                var transformedData = await TransformImportDataAsync(sourceData, request);

                // Validate data
                var validationResult = await _validationService.ValidateImportDataAsync(transformedData, request.ValidationRules);

                if (!validationResult.IsValid && !request.SkipInvalid)
                {
                    throw new TrainingDataException(ErrorCodes.IMPORT_VALIDATION_FAILED,
                        $"Import validation failed: {validationResult.ErrorMessage}");
                }

                // Filter invalid items if requested
                if (request.SkipInvalid && !validationResult.IsValid)
                {
                    transformedData = transformedData.Where((item, index) =>
                        !validationResult.InvalidIndices.Contains(index)).ToList();
                }

                // Create or update dataset
                if (string.IsNullOrEmpty(request.TargetDatasetId))
                {
                    result.CreatedDatasetId = await CreateDatasetFromImportAsync(transformedData, request);
                }
                else
                {
                    result.UpdatedDatasetId = request.TargetDatasetId;
                    await AddImportedDataToDatasetAsync(request.TargetDatasetId, transformedData, request);
                }

                // Update result
                result.CompletedAt = DateTime.UtcNow;
                result.TotalItemsRead = sourceData.Count;
                result.ValidItemsImported = transformedData.Count;
                result.InvalidItemsSkipped = validationResult.InvalidIndices.Count;
                result.ValidationResult = validationResult;

                // Update metrics
                _metrics.DataImports++;
                _metrics.TotalItemsImported += transformedData.Count;

                await _auditLogger.LogSystemEventAsync(
                    "DATA_IMPORT_COMPLETED",
                    $"Data import completed: {transformedData.Count} items imported",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Data import completed: {Imported} items imported from {Source}",
                    transformedData.Count, request.Source);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to import data from {Source}", request.Source);
                throw new TrainingDataException(ErrorCodes.IMPORT_FAILED,
                    $"Failed to import data from {request.Source}", ex);
            }
        }

        /// <summary>
        /// Dataset'i siler
        /// </summary>
        public async Task<bool> DeleteDatasetAsync(string datasetId, bool permanent = false)
        {
            if (string.IsNullOrWhiteSpace(datasetId))
                throw new ArgumentException("Dataset ID cannot be null or empty", nameof(datasetId));

            try
            {
                // Get dataset
                var dataset = await GetDatasetAsync(datasetId);
                if (dataset == null)
                {
                    _logger.LogWarning("Dataset not found for deletion: {DatasetId}", datasetId);
                    return false;
                }

                // Check if dataset is in use
                if (IsDatasetInUse(datasetId))
                {
                    throw new TrainingDataException(ErrorCodes.DATASET_IN_USE,
                        $"Dataset {datasetId} is currently in use and cannot be deleted");
                }

                // Delete data files
                if (permanent && Directory.Exists(dataset.StorageInfo.Path))
                {
                    Directory.Delete(dataset.StorageInfo.Path, true);
                }
                else
                {
                    // Mark as deleted
                    dataset.Status = DatasetStatus.Deleted;
                    dataset.DeletedAt = DateTime.UtcNow;
                }

                // Remove from collections
                _datasets.TryRemove(datasetId, out _);
                _datasetCache.Remove(datasetId);
                _statisticsCache.Remove(datasetId);

                // Update metrics
                _metrics.DatasetsDeleted++;

                await _auditLogger.LogSystemEventAsync(
                    "DATASET_DELETED",
                    $"Dataset deleted: {dataset.Name} (ID: {datasetId})",
                    permanent ? AuditLogSeverity.Warning : AuditLogSeverity.Information);

                _logger.LogInformation("Dataset deleted: {Name} (ID: {Id})", dataset.Name, datasetId);

                return true;
            }
            catch (TrainingDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete dataset {DatasetId}", datasetId);
                throw new TrainingDataException(ErrorCodes.DATASET_DELETION_FAILED,
                    $"Failed to delete dataset {datasetId}", ex);
            }
        }

        /// <summary>
        /// Data pipeline oluşturur
        /// </summary>
        public async Task<DataPipeline> CreatePipelineAsync(PipelineRequest request)
        {
            ValidatePipelineRequest(request);

            try
            {
                var pipelineId = GeneratePipelineId(request.Name);

                var pipeline = new DataPipeline
                {
                    Id = pipelineId,
                    Name = request.Name,
                    Description = request.Description,
                    Steps = request.Steps,
                    Configuration = request.Configuration,
                    Status = PipelineStatus.Created,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    Statistics = new PipelineStatistics()
                };

                // Validate pipeline steps
                await ValidatePipelineStepsAsync(pipeline);

                // Add to pipelines
                _pipelines[pipelineId] = pipeline;

                // Update metrics
                _metrics.PipelinesCreated++;

                await _auditLogger.LogSystemEventAsync(
                    "DATA_PIPELINE_CREATED",
                    $"Data pipeline created: {pipeline.Name} (ID: {pipelineId})",
                    AuditLogSeverity.Information);

                _logger.LogInformation("Data pipeline created: {Name} (ID: {Id})", pipeline.Name, pipelineId);

                return pipeline;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create data pipeline: {Name}", request.Name);
                throw new TrainingDataException(ErrorCodes.PIPELINE_CREATION_FAILED,
                    $"Failed to create data pipeline: {request.Name}", ex);
            }
        }

        /// <summary>
        /// Data pipeline'ı çalıştırır
        /// </summary>
        public async Task<PipelineExecutionResult> ExecutePipelineAsync(string pipelineId, PipelineExecutionRequest request)
        {
            ValidatePipelineExecutionRequest(pipelineId, request);

            try
            {
                // Get pipeline
                if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
                {
                    throw new TrainingDataException(ErrorCodes.PIPELINE_NOT_FOUND,
                        $"Pipeline not found: {pipelineId}");
                }

                // Check pipeline status
                if (pipeline.Status == PipelineStatus.Running)
                {
                    throw new TrainingDataException(ErrorCodes.PIPELINE_ALREADY_RUNNING,
                        $"Pipeline already running: {pipelineId}");
                }

                // Update pipeline status
                pipeline.Status = PipelineStatus.Running;
                pipeline.LastExecutionStarted = DateTime.UtcNow;

                var result = new PipelineExecutionResult
                {
                    PipelineId = pipelineId,
                    ExecutionId = Guid.NewGuid().ToString(),
                    StartedAt = DateTime.UtcNow,
                    Steps = new List<PipelineStepResult>()
                };

                // Execute pipeline steps
                object currentData = null;

                foreach (var step in pipeline.Steps)
                {
                    var stepResult = await ExecutePipelineStepAsync(step, currentData, request);
                    result.Steps.Add(stepResult);

                    if (!stepResult.Success)
                    {
                        result.Success = false;
                        result.ErrorMessage = $"Step failed: {step.Name} - {stepResult.ErrorMessage}";
                        break;
                    }

                    currentData = stepResult.Output;
                }

                // Update pipeline status
                pipeline.Status = result.Success ? PipelineStatus.Completed : PipelineStatus.Failed;
                pipeline.LastExecutionCompleted = DateTime.UtcNow;
                pipeline.LastExecutionResult = result;
                pipeline.Statistics.TotalExecutions++;

                if (result.Success)
                {
                    pipeline.Statistics.SuccessfulExecutions++;
                    result.OutputDatasetId = ExtractOutputDatasetId(result);
                }
                else
                {
                    pipeline.Statistics.FailedExecutions++;
                }

                result.CompletedAt = DateTime.UtcNow;
                result.Duration = result.CompletedAt - result.StartedAt;

                // Update metrics
                _metrics.PipelineExecutions++;

                await _auditLogger.LogSystemEventAsync(
                    result.Success ? "PIPELINE_EXECUTION_COMPLETED" : "PIPELINE_EXECUTION_FAILED",
                    $"Pipeline execution {(result.Success ? "completed" : "failed")}: {pipeline.Name}",
                    result.Success ? AuditLogSeverity.Information : AuditLogSeverity.Error);

                _logger.LogInformation("Pipeline execution {Status}: {PipelineName}",
                    result.Success ? "completed" : "failed", pipeline.Name);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to execute pipeline {PipelineId}", pipelineId);
                throw new TrainingDataException(ErrorCodes.PIPELINE_EXECUTION_FAILED,
                    $"Failed to execute pipeline {pipelineId}", ex);
            }
        }

        /// <summary>
        /// Data manager metriklerini getirir
        /// </summary>
        public async Task<DataManagerMetrics> GetMetricsAsync(bool detailed = false)
        {
            var metrics = (DataManagerMetrics)_metrics.Clone();

            if (detailed)
            {
                metrics.ActiveDatasets = _datasets.Values
                    .Where(d => d.Status == DatasetStatus.Active || d.Status == DatasetStatus.Partial)
                    .Count();

                metrics.ActiveBatches = _activeBatches.Values
                    .Where(b => b.Status == BatchStatus.Preparing || b.Status == BatchStatus.Ready)
                    .Count();

                metrics.ActivePipelines = _pipelines.Values
                    .Where(p => p.Status == PipelineStatus.Running)
                    .Count();

                metrics.QueuedJobs = _processingQueue.Count;

                metrics.CacheStatistics = new CacheStatistics
                {
                    DatasetCacheHits = _datasetCache.HitCount,
                    DatasetCacheMisses = _datasetCache.MissCount,
                    StatisticsCacheHits = _statisticsCache.HitCount,
                    StatisticsCacheMisses = _statisticsCache.MissCount,
                    DatasetCacheSize = _datasetCache.Count,
                    StatisticsCacheSize = _statisticsCache.Count
                };
            }

            metrics.Uptime = _metrics.StartTime.HasValue
                ? DateTime.UtcNow - _metrics.StartTime.Value
                : TimeSpan.Zero;

            return metrics;
        }

        #region Private Methods - Core Operations

        private void Initialize()
        {
            try
            {
                // Create base directories
                EnsureDirectories();

                // Initialize cache
                _datasetCache.OnItemRemoved += OnDatasetCacheItemRemoved;
                _statisticsCache.OnItemRemoved += OnStatisticsCacheItemRemoved;

                _metrics.Status = DataManagerStatus.Initializing;

                _logger.LogInformation("Training data manager initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize training data manager");
                throw new TrainingDataException(ErrorCodes.INITIALIZATION_FAILED,
                    "Failed to initialize training data manager", ex);
            }
        }

        private void EnsureDirectories()
        {
            try
            {
                if (!Directory.Exists(_baseDataPath))
                {
                    Directory.CreateDirectory(_baseDataPath);
                }

                if (!Directory.Exists(_tempPath))
                {
                    Directory.CreateDirectory(_tempPath);
                }

                // Create subdirectories
                var subdirs = new[] { "datasets", "exports", "imports", "backups", "logs" };
                foreach (var subdir in subdirs)
                {
                    var path = Path.Combine(_baseDataPath, subdir);
                    if (!Directory.Exists(path))
                    {
                        Directory.CreateDirectory(path);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create data directories");
                throw new TrainingDataException(ErrorCodes.DIRECTORY_CREATION_FAILED,
                    "Failed to create data directories", ex);
            }
        }

        private void EnsureDatasetDirectory(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);

                    // Create subdirectories
                    var subdirs = new[] { "data", "metadata", "indices", "backups" };
                    foreach (var subdir in subdirs)
                    {
                        Directory.CreateDirectory(Path.Combine(path, subdir));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create dataset directory: {Path}", path);
                throw new TrainingDataException(ErrorCodes.DATASET_DIRECTORY_CREATION_FAILED,
                    $"Failed to create dataset directory: {path}", ex);
            }
        }

        private async Task LoadExistingDatasetsAsync()
        {
            try
            {
                var datasetsPath = Path.Combine(_baseDataPath, "datasets");
                if (!Directory.Exists(datasetsPath))
                    return;

                var datasetDirs = Directory.GetDirectories(datasetsPath);
                var loadedCount = 0;

                foreach (var dir in datasetDirs)
                {
                    try
                    {
                        var metadataPath = Path.Combine(dir, "metadata", "dataset.json");
                        if (File.Exists(metadataPath))
                        {
                            var json = await File.ReadAllTextAsync(metadataPath);
                            var dataset = JsonSerializer.Deserialize<Dataset>(json);

                            if (dataset != null && dataset.Status != DatasetStatus.Deleted)
                            {
                                _datasets[dataset.Id] = dataset;
                                loadedCount++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to load dataset from directory: {Directory}", dir);
                    }
                }

                _logger.LogInformation("Loaded {Count} existing datasets", loadedCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load existing datasets");
            }
        }

        private async Task InitializeDataSourcesAsync()
        {
            try
            {
                // Register built-in data sources
                RegisterBuiltInDataSources();

                // Initialize all data sources
                foreach (var source in _dataSources)
                {
                    try
                    {
                        await source.InitializeAsync();
                        _logger.LogDebug("Data source initialized: {SourceName}", source.GetType().Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to initialize data source: {SourceName}", source.GetType().Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize data sources");
            }
        }

        private void RegisterBuiltInDataSources()
        {
            // Register file system data source
            var fileSystemSource = new FileSystemDataSource(_baseDataPath);
            _dataSources.Add(fileSystemSource);

            _logger.LogDebug("Registered {Count} built-in data sources", _dataSources.Count);
        }

        private void StartBackgroundTasks()
        {
            _cleanupTask = Task.Run(() => CleanupTaskAsync(_backgroundCts.Token));
            _indexingTask = Task.Run(() => IndexingTaskAsync(_backgroundCts.Token));
        }

        private void StartProcessingQueue()
        {
            for (int i = 0; i < _config.MaxConcurrentProcessings; i++)
            {
                Task.Run(() => ProcessQueueAsync(_backgroundCts.Token));
            }
        }

        private async Task CleanupTaskAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Cleanup task started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_config.CleanupIntervalMinutes), cancellationToken);

                    // Cleanup old temporary files
                    await CleanupTempFilesAsync();

                    // Cleanup expired batches
                    await CleanupExpiredBatchesAsync();

                    // Cleanup old cache entries
                    CleanupCache();

                    // Cleanup old exports
                    await CleanupOldExportsAsync();

                    _logger.LogDebug("Cleanup task completed");
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in cleanup task");
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
            }

            _logger.LogInformation("Cleanup task stopped");
        }

        private async Task IndexingTaskAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Indexing task started");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(_config.IndexingIntervalMinutes), cancellationToken);

                    // Reindex datasets
                    await ReindexDatasetsAsync();

                    // Update statistics
                    await UpdateAllDatasetStatisticsAsync();

                    // Optimize storage
                    await OptimizeStorageAsync();

                    _logger.LogDebug("Indexing task completed");
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in indexing task");
                    await Task.Delay(TimeSpan.FromMinutes(10), cancellationToken);
                }
            }

            _logger.LogInformation("Indexing task stopped");
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_processingQueue.TryDequeue(out var job))
                    {
                        await ProcessJobAsync(job, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(100, cancellationToken);
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in processing queue");
                    await Task.Delay(1000, cancellationToken);
                }
            }
        }

        #endregion

        #region Private Methods - Data Processing

        private async Task ProcessAndStoreDataAsync(Dataset dataset, CreateDatasetRequest request)
        {
            try
            {
                dataset.Status = DatasetStatus.Processing;

                // Load data from source
                List<DataItem> dataItems;

                if (request.Data != null)
                {
                    dataItems = await ProcessRawDataAsync(request.Data, request.Format, request.ProcessingOptions);
                }
                else if (!string.IsNullOrEmpty(request.DataSource))
                {
                    dataItems = await LoadDataFromSourceAsync(request.DataSource, request.Format, request.ProcessingOptions);
                }
                else
                {
                    throw new TrainingDataException(ErrorCodes.NO_DATA_PROVIDED,
                        "No data provided for dataset creation");
                }

                // Validate data
                var validationResult = await _validationService.ValidateDataAsync(dataItems, dataset.DataType);
                if (!validationResult.IsValid)
                {
                    throw new TrainingDataException(ErrorCodes.DATA_VALIDATION_FAILED,
                        $"Data validation failed: {validationResult.ErrorMessage}");
                }

                // Apply processing if specified
                if (request.ProcessingOptions != null && request.ProcessingOptions.Any())
                {
                    dataItems = await _transformationService.ProcessAsync(dataItems, request.ProcessingOptions);
                }

                // Store data
                await StoreDataItemsAsync(dataset, dataItems);

                // Update dataset
                dataset.TotalItems = dataItems.Count;
                dataset.Status = DatasetStatus.Active;
                dataset.UpdatedAt = DateTime.UtcNow;

                // Calculate initial statistics
                await CalculateInitialStatisticsAsync(dataset, dataItems);

                _logger.LogDebug("Processed and stored {Count} items for dataset {DatasetId}",
                    dataItems.Count, dataset.Id);
            }
            catch (Exception ex)
            {
                dataset.Status = DatasetStatus.Error;
                dataset.ErrorMessage = ex.Message;
                throw;
            }
        }

        private async Task<int> ProcessAndAddDataAsync(Dataset dataset, AddDataRequest request)
        {
            try
            {
                // Load new data
                List<DataItem> newItems;

                if (request.Data != null)
                {
                    newItems = await ProcessRawDataAsync(request.Data, request.Format, request.ProcessingOptions);
                }
                else if (!string.IsNullOrEmpty(request.DataSource))
                {
                    newItems = await LoadDataFromSourceAsync(request.DataSource, request.Format, request.ProcessingOptions);
                }
                else
                {
                    throw new TrainingDataException(ErrorCodes.NO_DATA_PROVIDED,
                        "No data provided for addition");
                }

                // Validate new data
                var validationResult = await _validationService.ValidateDataAsync(newItems, dataset.DataType);
                if (!validationResult.IsValid && !request.SkipInvalid)
                {
                    throw new TrainingDataException(ErrorCodes.DATA_VALIDATION_FAILED,
                        $"Data validation failed: {validationResult.ErrorMessage}");
                }

                // Filter invalid items if requested
                if (request.SkipInvalid && !validationResult.IsValid)
                {
                    newItems = newItems.Where((item, index) =>
                        !validationResult.InvalidIndices.Contains(index)).ToList();
                }

                // Check for duplicates
                if (request.RemoveDuplicates)
                {
                    newItems = await RemoveDuplicatesAsync(dataset, newItems);
                }

                // Append to existing data
                await AppendDataItemsAsync(dataset, newItems);

                return newItems.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process and add data to dataset {DatasetId}", dataset.Id);
                throw;
            }
        }

        private async Task LoadBatchDataAsync(DataBatch batch, Dataset dataset, BatchRequest request)
        {
            try
            {
                // Determine which items to load
                var itemIndices = await SelectBatchItemsAsync(dataset, request);

                // Load items
                var items = new List<DataItem>();
                foreach (var index in itemIndices)
                {
                    var item = await LoadDataItemAsync(dataset, index);
                    if (item != null)
                    {
                        items.Add(item);
                    }
                }

                // Apply sampling if needed
                if (request.SampleRate.HasValue && request.SampleRate.Value < 1.0)
                {
                    items = SampleItems(items, request.SampleRate.Value, request.RandomSeed);
                }

                // Apply batching
                if (request.BatchSize > 0 && items.Count > request.BatchSize)
                {
                    items = items.Take(request.BatchSize).ToList();
                }

                // Update batch
                batch.Items = items;
                batch.ActualSize = items.Count;
                batch.Indices = itemIndices.Take(items.Count).ToList();

                _logger.LogDebug("Loaded {Count} items for batch {BatchId}", items.Count, batch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load batch data for dataset {DatasetId}", dataset.Id);
                throw;
            }
        }

        private async Task ApplyTransformationsAsync(DataBatch batch, List<Transformation> transformations)
        {
            try
            {
                var transformedItems = await _transformationService.TransformAsync(batch.Items, transformations);
                batch.Items = transformedItems;
                batch.TransformationsApplied = transformations;

                _logger.LogDebug("Applied {Count} transformations to batch {BatchId}",
                    transformations.Count, batch.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply transformations to batch {BatchId}", batch.Id);
                throw new TrainingDataException(ErrorCodes.TRANSFORMATION_APPLICATION_FAILED,
                    $"Failed to apply transformations to batch {batch.Id}", ex);
            }
        }

        private async Task ApplyAugmentationsAsync(DataBatch batch, List<Augmentation> augmentations)
        {
            try
            {
                var augmentedItems = await _augmentationService.AugmentAsync(batch.Items, augmentations);

                // Combine original and augmented items
                var combinedItems = new List<DataItem>();
                combinedItems.AddRange(batch.Items);
                combinedItems.AddRange(augmentedItems);

                batch.Items = combinedItems;
                batch.AugmentationsApplied = augmentations;
                batch.AugmentedCount = augmentedItems.Count;

                _logger.LogDebug("Applied {Count} augmentations to batch {BatchId}, added {AugmentedCount} items",
                    augmentations.Count, batch.Id, augmentedItems.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply augmentations to batch {BatchId}", batch.Id);
                throw new TrainingDataException(ErrorCodes.AUGMENTATION_APPLICATION_FAILED,
                    $"Failed to apply augmentations to batch {batch.Id}", ex);
            }
        }

        #endregion

        #region Private Methods - Helper Methods

        private string GenerateDatasetId(string name)
        {
            var sanitizedName = SanitizeFileName(name);
            var hash = ComputeHash(sanitizedName + DateTime.UtcNow.Ticks);
            return $"{sanitizedName}_{hash.Substring(0, 8)}".ToLower();
        }

        private string GeneratePipelineId(string name)
        {
            var sanitizedName = SanitizeFileName(name);
            var hash = ComputeHash(sanitizedName + DateTime.UtcNow.Ticks);
            return $"pipeline_{sanitizedName}_{hash.Substring(0, 6)}".ToLower();
        }

        private string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var sanitized = new string(
                fileName
                    .Where(ch => !invalidChars.Contains(ch))
                    .ToArray());

            return string.IsNullOrEmpty(sanitized) ? "dataset" : sanitized;
        }

        private string ComputeHash(string input)
        {
            using var sha256 = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = sha256.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLower();
        }

        private async Task<Dataset> GetDatasetAsync(string datasetId)
        {
            // Check memory cache
            if (_datasets.TryGetValue(datasetId, out var dataset))
            {
                return dataset;
            }

            // Check disk cache
            if (_datasetCache.TryGet(datasetId, out var cachedDataset))
            {
                return cachedDataset.Dataset;
            }

            // Load from storage
            return await LoadDatasetFromStorageAsync(datasetId);
        }

        private async Task CacheDatasetAsync(Dataset dataset)
        {
            var cachedDataset = new CachedDataset
            {
                Dataset = dataset,
                CachedAt = DateTime.UtcNow,
                AccessCount = 0
            };

            _datasetCache.Set(dataset.Id, cachedDataset, TimeSpan.FromMinutes(_config.DatasetCacheMinutes));
        }

        private bool IsDatasetInUse(string datasetId)
        {
            // Check active batches
            if (_activeBatches.Values.Any(b => b.DatasetId == datasetId &&
                (b.Status == BatchStatus.Preparing || b.Status == BatchStatus.Ready)))
            {
                return true;
            }

            // Check processing jobs
            if (_processingQueue.Any(j => j.DatasetId == datasetId))
            {
                return true;
            }

            // Check pipelines
            if (_pipelines.Values.Any(p =>
                p.Status == PipelineStatus.Running &&
                p.Steps.Any(s => s.DatasetId == datasetId)))
            {
                return true;
            }

            return false;
        }

        private void OnDatasetCacheItemRemoved(object sender, CacheItemRemovedEventArgs<string, CachedDataset> e)
        {
            _logger.LogDebug("Dataset cache item removed: {Key}, Reason: {Reason}",
                e.Key, e.Reason);
        }

        private void OnStatisticsCacheItemRemoved(object sender, CacheItemRemovedEventArgs<string, DataStatistics> e)
        {
            _logger.LogDebug("Statistics cache item removed: {Key}, Reason: {Reason}",
                e.Key, e.Reason);
        }

        #endregion

        #region Private Methods - Stub Implementations

        // These methods would have full implementations in a complete system
        private async Task CleanupTempFilesAsync() => await Task.CompletedTask;
        private async Task CleanupExpiredBatchesAsync() => await Task.CompletedTask;
        private void CleanupCache() { }
        private async Task CleanupOldExportsAsync() => await Task.CompletedTask;
        private async Task ReindexDatasetsAsync() => await Task.CompletedTask;
        private async Task UpdateAllDatasetStatisticsAsync() => await Task.CompletedTask;
        private async Task OptimizeStorageAsync() => await Task.CompletedTask;
        private async Task ProcessJobAsync(DataProcessingJob job, CancellationToken cancellationToken) => await Task.CompletedTask;
        private async Task<List<DataItem>> ProcessRawDataAsync(object data, string format, Dictionary<string, object> options) => new List<DataItem>();
        private async Task<List<DataItem>> LoadDataFromSourceAsync(string source, string format, Dictionary<string, object> options) => new List<DataItem>();
        private async Task StoreDataItemsAsync(Dataset dataset, List<DataItem> items) => await Task.CompletedTask;
        private async Task CalculateInitialStatisticsAsync(Dataset dataset, List<DataItem> items) => await Task.CompletedTask;
        private async Task<List<int>> SelectBatchItemsAsync(Dataset dataset, BatchRequest request) => new List<int>();
        private async Task<DataItem> LoadDataItemAsync(Dataset dataset, int index) => new DataItem();
        private List<DataItem> SampleItems(List<DataItem> items, double sampleRate, int? randomSeed) => items;
        private async Task<List<DataItem>> RemoveDuplicatesAsync(Dataset dataset, List<DataItem> newItems) => newItems;
        private async Task AppendDataItemsAsync(Dataset dataset, List<DataItem> newItems) => await Task.CompletedTask;
        private async Task UpdateDatasetStatisticsAsync(Dataset dataset) => await Task.CompletedTask;
        private async Task PerformDatasetSplitAsync(DatasetSplit split, Dataset dataset, SplitRequest request) => await Task.CompletedTask;
        private async Task CreateSplitDatasetsAsync(DatasetSplit split, Dataset dataset) => await Task.CompletedTask;
        private async Task<List<DataItem>> GetDataForAugmentationAsync(AugmentationRequest request) => new List<DataItem>();
        private async Task<string> StoreAugmentedDataAsync(List<DataItem> augmentedData, AugmentationRequest request) => string.Empty;
        private async Task<ProcessingJobResult> WaitForJobCompletionAsync(string jobId) => new ProcessingJobResult();
        private async Task<List<DataItem>> LoadDatasetDataAsync(Dataset dataset, BatchRequest request) => new List<DataItem>();
        private async Task<string> CreateCleanedDatasetAsync(Dataset dataset, List<DataItem> cleanedData, CleaningRequest request) => string.Empty;
        private async Task ReplaceDatasetDataAsync(Dataset dataset, List<DataItem> cleanedData) => await Task.CompletedTask;
        private async Task<Dictionary<string, int>> AnalyzeRemovedItemsAsync(List<DataItem> original, List<DataItem> cleaned) => new Dictionary<string, int>();
        private async Task<List<DataItem>> ApplyFiltersAsync(List<DataItem> data, List<DataFilter> filters) => data;
        private async Task<object> TransformToExportFormatAsync(List<DataItem> data, string format, Dictionary<string, object> options) => new object();
        private async Task<string> WriteExportFileAsync(object exportData, ExportRequest request) => string.Empty;
        private async Task<List<DataItem>> ReadImportSourceAsync(ImportRequest request) => new List<DataItem>();
        private async Task<List<DataItem>> TransformImportDataAsync(List<DataItem> sourceData, ImportRequest request) => sourceData;
        private async Task<string> CreateDatasetFromImportAsync(List<DataItem> data, ImportRequest request) => string.Empty;
        private async Task AddImportedDataToDatasetAsync(string datasetId, List<DataItem> data, ImportRequest request) => await Task.CompletedTask;
        private async Task ValidatePipelineStepsAsync(DataPipeline pipeline) => await Task.CompletedTask;
        private async Task<PipelineStepResult> ExecutePipelineStepAsync(PipelineStep step, object input, PipelineExecutionRequest request) => new PipelineStepResult();
        private string ExtractOutputDatasetId(PipelineExecutionResult result) => string.Empty;
        private async Task<Dataset> LoadDatasetFromStorageAsync(string datasetId) => null;

        #endregion

        #region Private Methods - Validation

        private void ValidateCreateDatasetRequest(CreateDatasetRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Dataset name cannot be null or empty", nameof(request.Name));
            if (string.IsNullOrWhiteSpace(request.DataType)) throw new ArgumentException("Data type cannot be null or empty", nameof(request.DataType));
            if (string.IsNullOrWhiteSpace(request.Domain)) throw new ArgumentException("Domain cannot be null or empty", nameof(request.Domain));
        }

        private void ValidateAddDataRequest(string datasetId, AddDataRequest request)
        {
            if (string.IsNullOrWhiteSpace(datasetId)) throw new ArgumentException("Dataset ID cannot be null or empty", nameof(datasetId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.Data == null && string.IsNullOrEmpty(request.DataSource)) throw new ArgumentException("Data or data source must be provided");
        }

        private void ValidateBatchRequest(string datasetId, BatchRequest request)
        {
            if (string.IsNullOrWhiteSpace(datasetId)) throw new ArgumentException("Dataset ID cannot be null or empty", nameof(datasetId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.BatchSize <= 0) throw new ArgumentException("Batch size must be greater than 0", nameof(request.BatchSize));
        }

        private void ValidateSplitRequest(string datasetId, SplitRequest request)
        {
            if (string.IsNullOrWhiteSpace(datasetId)) throw new ArgumentException("Dataset ID cannot be null or empty", nameof(datasetId));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.TrainRatio + request.ValidationRatio + request.TestRatio != 1.0)
                throw new ArgumentException("Split ratios must sum to 1.0");
            if (request.TrainRatio <= 0 || request.ValidationRatio < 0 || request.TestRatio < 0)
                throw new ArgumentException("Split ratios must be non-negative and at least one must be positive");
        }

        private void ValidateAugmentationRequest(AugmentationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.SourceDatasetId) && (request.SourceData == null || !request.SourceData.Any()))
                throw new ArgumentException("Source dataset ID or source data must be provided");
            if (request.Augmentations == null || !request.Augmentations.Any())
                throw new ArgumentException("At least one augmentation must be specified");
        }

        private void ValidateTransformationRequest(TransformationRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.DatasetId)) throw new ArgumentException("Dataset ID cannot be null or empty", nameof(request.DatasetId));
            if (request.Transformations == null || !request.Transformations.Any())
                throw new ArgumentException("At least one transformation must be specified");
        }

        private void ValidateCleaningRequest(CleaningRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.DatasetId)) throw new ArgumentException("Dataset ID cannot be null or empty", nameof(request.DatasetId));
            if (request.CleaningRules == null || !request.CleaningRules.Any())
                throw new ArgumentException("At least one cleaning rule must be specified");
        }

        private void ValidateExportRequest(ExportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.DatasetId)) throw new ArgumentException("Dataset ID cannot be null or empty", nameof(request.DatasetId));
            if (string.IsNullOrWhiteSpace(request.Format)) throw new ArgumentException("Export format cannot be null or empty", nameof(request.Format));
        }

        private void ValidateImportRequest(ImportRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Source)) throw new ArgumentException("Source cannot be null or empty", nameof(request.Source));
            if (string.IsNullOrWhiteSpace(request.Format)) throw new ArgumentException("Format cannot be null or empty", nameof(request.Format));
        }

        private void ValidatePipelineRequest(PipelineRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Pipeline name cannot be null or empty", nameof(request.Name));
            if (request.Steps == null || !request.Steps.Any())
                throw new ArgumentException("At least one pipeline step must be specified");
        }

        private void ValidatePipelineExecutionRequest(string pipelineId, PipelineExecutionRequest request)
        {
            if (string.IsNullOrWhiteSpace(pipelineId)) throw new ArgumentException("Pipeline ID cannot be null or empty", nameof(pipelineId));
            if (request == null) throw new ArgumentNullException(nameof(request));
        }

        #endregion

        #region IDisposable Implementation

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Cleanup managed resources
                    _backgroundCts?.Cancel();
                    _backgroundCts?.Dispose();

                    _processingSemaphore?.Dispose();

                    try
                    {
                        _cleanupTask?.Wait(TimeSpan.FromSeconds(5));
                        _indexingTask?.Wait(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error waiting for background tasks to complete");
                    }

                    // Clear collections
                    _datasets.Clear();
                    _activeBatches.Clear();
                    _pipelines.Clear();
                    _datasetCache.Clear();
                    _statisticsCache.Clear();

                    while (_processingQueue.TryDequeue(out _)) { }

                    _dataSources.Clear();
                    _dataAdapters.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~TrainingDataManager()
        {
            Dispose(false);
        }

        #endregion
    }

    #region Supporting Classes and Interfaces

    /// <summary>
    /// Training data manager configuration
    /// </summary>
    public class TrainingDataConfig
    {
        public string BaseDataPath { get; set; } = "Data/Training";
        public int MaxCacheSize { get; set; } = 1000;
        public int MaxStatisticsCache { get; set; } = 500;
        public int DatasetCacheMinutes { get; set; } = 60;
        public int StatisticsCacheMinutes { get; set; } = 30;
        public int MaxConcurrentProcessings { get; set; } = 4;
        public int CleanupIntervalMinutes { get; set; } = 60;
        public int IndexingIntervalMinutes { get; set; } = 30;
        public int DefaultRetentionDays { get; set; } = 365;
        public int MaxDatasetSize { get; set; } = 1000000; // 1 million items
        public int MaxBatchSize { get; set; } = 10000;
        public bool EnableCompression { get; set; } = true;
        public bool EnableEncryption { get; set; } = false;
        public string DefaultFormat { get; set; } = "json";
    }

    /// <summary>
    /// Training data manager interface
    /// </summary>
    public interface ITrainingDataManager : IDisposable
    {
        Task InitializeAsync();
        Task<Dataset> CreateDatasetAsync(CreateDatasetRequest request);
        Task<bool> AddDataToDatasetAsync(string datasetId, AddDataRequest request);
        Task<DataBatch> GetDataBatchAsync(string datasetId, BatchRequest request);
        Task<DataStatistics> GetDatasetStatisticsAsync(string datasetId);
        Task<DatasetSplit> SplitDatasetAsync(string datasetId, SplitRequest request);
        Task<AugmentationResult> AugmentDataAsync(AugmentationRequest request);
        Task<TransformationResult> TransformDatasetAsync(TransformationRequest request);
        Task<CleaningResult> CleanDatasetAsync(CleaningRequest request);
        Task<ExportResult> ExportDatasetAsync(ExportRequest request);
        Task<ImportResult> ImportDataAsync(ImportRequest request);
        Task<bool> DeleteDatasetAsync(string datasetId, bool permanent = false);
        Task<DataPipeline> CreatePipelineAsync(PipelineRequest request);
        Task<PipelineExecutionResult> ExecutePipelineAsync(string pipelineId, PipelineExecutionRequest request);
        Task<DataManagerMetrics> GetMetricsAsync(bool detailed = false);
    }

    /// <summary>
    /// Dataset
    /// </summary>
    public class Dataset
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string DataType { get; set; } // "image", "text", "audio", "video", "tabular", "time-series"
        public string Domain { get; set; }
        public string Format { get; set; }
        public int Version { get; set; }
        public DatasetStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }
        public long TotalItems { get; set; }
        public DataStatistics Statistics { get; set; }
        public StorageInfo StorageInfo { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Dataset status
    /// </summary>
    public enum DatasetStatus
    {
        Creating,
        Processing,
        Active,
        Partial,
        Updating,
        Cleaning,
        Exporting,
        Importing,
        Archived,
        Deleted,
        Error
    }

    /// <summary>
    /// Data batch
    /// </summary>
    public class DataBatch
    {
        public string Id { get; set; }
        public string DatasetId { get; set; }
        public int DatasetVersion { get; set; }
        public BatchRequest Request { get; set; }
        public BatchStatus Status { get; set; }
        public List<DataItem> Items { get; set; } = new List<DataItem>();
        public List<int> Indices { get; set; } = new List<int>();
        public int ActualSize { get; set; }
        public List<Transformation> TransformationsApplied { get; set; } = new List<Transformation>();
        public List<Augmentation> AugmentationsApplied { get; set; } = new List<Augmentation>();
        public int AugmentedCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? PreparedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Batch status
    /// </summary>
    public enum BatchStatus
    {
        Preparing,
        Ready,
        InUse,
        Expired,
        Failed
    }

    /// <summary>
    /// Data item
    /// </summary>
    public class DataItem
    {
        public string Id { get; set; }
        public string DatasetId { get; set; }
        public int Index { get; set; }
        public object Data { get; set; }
        public object Labels { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public DataQuality Quality { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>
    /// Data quality
    /// </summary>
    public class DataQuality
    {
        public double Score { get; set; }
        public List<string> Issues { get; set; } = new List<string>();
        public bool IsValid { get; set; }
        public DateTime LastChecked { get; set; }
    }

    /// <summary>
    /// Data statistics
    /// </summary>
    public class DataStatistics
    {
        public long TotalItems { get; set; }
        public long ValidItems { get; set; }
        public long InvalidItems { get; set; }
        public Dictionary<string, long> LabelDistribution { get; set; } = new Dictionary<string, long>();
        public Dictionary<string, StatisticalSummary> FeatureStatistics { get; set; } = new Dictionary<string, StatisticalSummary>();
        public DataQualitySummary QualitySummary { get; set; }
        public DateTime CalculatedAt { get; set; }
    }

    /// <summary>
    /// Statistical summary
    /// </summary>
    public class StatisticalSummary
    {
        public double Mean { get; set; }
        public double Median { get; set; }
        public double StandardDeviation { get; set; }
        public double Min { get; set; }
        public double Max { get; set; }
        public long Count { get; set; }
        public long Missing { get; set; }
        public Dictionary<string, long> ValueCounts { get; set; } = new Dictionary<string, long>();
    }

    /// <summary>
    /// Data quality summary
    /// </summary>
    public class DataQualitySummary
    {
        public double AverageQualityScore { get; set; }
        public double MinQualityScore { get; set; }
        public double MaxQualityScore { get; set; }
        public Dictionary<string, long> IssueDistribution { get; set; } = new Dictionary<string, long>();
        public long ItemsWithIssues { get; set; }
        public long ItemsWithoutIssues { get; set; }
    }

    /// <summary>
    /// Storage information
    /// </summary>
    public class StorageInfo
    {
        public string Path { get; set; }
        public bool Compression { get; set; }
        public bool Encryption { get; set; }
        public int RetentionDays { get; set; }
        public long SizeBytes { get; set; }
        public int FileCount { get; set; }
        public DateTime LastBackup { get; set; }
    }

    /// <summary>
    /// Create dataset request
    /// </summary>
    public class CreateDatasetRequest
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string DataType { get; set; }
        public string Domain { get; set; }
        public string Format { get; set; }
        public object Data { get; set; }
        public string DataSource { get; set; }
        public bool Compression { get; set; } = true;
        public bool Encryption { get; set; } = false;
        public int? RetentionDays { get; set; }
        public Dictionary<string, object> ProcessingOptions { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Add data request
    /// </summary>
    public class AddDataRequest
    {
        public object Data { get; set; }
        public string DataSource { get; set; }
        public string Format { get; set; }
        public int ExpectedCount { get; set; }
        public bool SkipInvalid { get; set; } = false;
        public bool RemoveDuplicates { get; set; } = true;
        public Dictionary<string, object> ProcessingOptions { get; set; }
    }

    /// <summary>
    /// Batch request
    /// </summary>
    public class BatchRequest
    {
        public int BatchSize { get; set; } = 32;
        public bool Shuffle { get; set; } = true;
        public int? RandomSeed { get; set; }
        public double? SampleRate { get; set; }
        public List<Transformation> Transformations { get; set; }
        public List<Augmentation> Augmentations { get; set; }
        public Dictionary<string, object> Options { get; set; }
    }

    /// <summary>
    /// Transformation
    /// </summary>
    public class Transformation
    {
        public string Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public int Order { get; set; }
    }

    /// <summary>
    /// Augmentation
    /// </summary>
    public class Augmentation
    {
        public string Type { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public double Probability { get; set; } = 1.0;
        public int Count { get; set; } = 1;
    }

    /// <summary>
    /// Data manager metrics
    /// </summary>
    public class DataManagerMetrics : ICloneable
    {
        public DataManagerStatus Status { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? StopTime { get; set; }
        public TimeSpan Uptime => StartTime.HasValue ?
            (StopTime ?? DateTime.UtcNow) - StartTime.Value : TimeSpan.Zero;

        // Counters
        public long DatasetsCreated { get; set; }
        public long DatasetsDeleted { get; set; }
        public long DatasetsCleaned { get; set; }
        public long DatasetsExported { get; set; }
        public long DatasetSplits { get; set; }
        public long DataImports { get; set; }
        public long TotalItemsImported { get; set; }
        public long DataItemsAdded { get; set; }
        public long TotalItemsServed { get; set; }
        public long BatchesServed { get; set; }
        public long DataAugmentations { get; set; }
        public long TotalAugmentedItems { get; set; }
        public long TotalItemsCleaned { get; set; }
        public long PipelinesCreated { get; set; }
        public long PipelineExecutions { get; set; }

        // Current state
        public int ActiveDatasets { get; set; }
        public int ActiveBatches { get; set; }
        public int ActivePipelines { get; set; }
        public int QueuedJobs { get; set; }
        public CacheStatistics CacheStatistics { get; set; }

        public object Clone()
        {
            return MemberwiseClone();
        }
    }

    /// <summary>
    /// Data manager status
    /// </summary>
    public enum DataManagerStatus
    {
        Stopped,
        Initializing,
        Running,
        Stopping,
        Error
    }

    /// <summary>
    /// Training data exception
    /// </summary>
    public class TrainingDataException : Exception
    {
        public string ErrorCode { get; }

        public TrainingDataException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }

        public TrainingDataException(string errorCode, string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    // Additional supporting classes would be defined here...

    #endregion
}
