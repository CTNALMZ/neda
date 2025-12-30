// NEDA.AI/ModelManagement/ModelManager.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ML;
using NEDA.AI.MachineLearning;
using NEDA.AI.ReinforcementLearning;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Configuration.EnvironmentManager;
using NEDA.Core.ExceptionHandling.GlobalExceptionHandler;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TensorFlow;
using TorchSharp;

namespace NEDA.AI.ModelManagement
{
    /// <summary>
    /// Merkezi model yönetim sistemi - Machine Learning modellerinin yaşam döngüsünü yönetir.
    /// </summary>
    public sealed class ModelManager : IModelManager, IDisposable
    {
        #region Singleton Implementation
        private static readonly Lazy<ModelManager> _instance =
            new Lazy<ModelManager>(() => new ModelManager());

        public static ModelManager Instance => _instance.Value;

        private ModelManager()
        {
            Initialize();
        }
        #endregion

        #region Fields and Properties
        private readonly ConcurrentDictionary<string, ModelContainer> _models =
            new ConcurrentDictionary<string, ModelContainer>();

        private readonly ConcurrentDictionary<string, ModelTrainingSession> _trainingSessions =
            new ConcurrentDictionary<string, ModelTrainingSession>();

        private readonly ConcurrentQueue<ModelDeploymentTask> _deploymentQueue =
            new ConcurrentQueue<ModelDeploymentTask>();

        private readonly ConcurrentDictionary<string, ModelPerformanceMetrics> _performanceMetrics =
            new ConcurrentDictionary<string, ModelPerformanceMetrics>();

        private readonly ReaderWriterLockSlim _modelLock = new ReaderWriterLockSlim();
        private readonly SemaphoreSlim _deploymentLock = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        private Task _deploymentTask;
        private Task _monitoringTask;
        private Task _cleanupTask;

        private bool _isInitialized = false;
        private bool _isDisposed = false;

        private AppConfig _appConfig;
        private IEnvironmentConfig _environmentConfig;
        private Microsoft.Extensions.Logging.ILogger _logger;
        private ISecurityManager _securityManager;
        private IRecoveryEngine _recoveryEngine;

        private string _modelRepositoryPath;
        private string _cachePath;
        private string _deploymentPath;

        private long _totalModelsLoaded;
        private DateTime _lastHealthCheck;

        public ModelRepositoryStatus RepositoryStatus { get; private set; }

        public int ActiveModelCount => _models.Count;
        public int PendingDeployments => _deploymentQueue.Count;
        public long TotalMemoryUsage { get; private set; }
        public bool IsAutoUpdateEnabled { get; private set; } = true;

        public event EventHandler<ModelEventArgs> ModelEvent;
        #endregion

        #region Public Methods
        /// <summary>
        /// ModelManager'ı başlatır ve yapılandırır.
        /// </summary>
        public void Initialize()
        {
            if (_isInitialized) return;

            try
            {
                _modelLock.EnterWriteLock();

                LoadDependencies();
                ConfigurePaths();
                InitializeRepository();
                ClearCache();
                LoadModels();

                StartDeploymentTask();
                StartMonitoringTask();
                StartCleanupTask();

                _isInitialized = true;

                LogModelEvent(
                    ModelEventType.SystemInitialized,
                    "ModelManager başarıyla başlatıldı",
                    modelId: null,
                    severity: ModelSeverity.Information);
            }
            catch (Exception ex)
            {
                HandleInitializationError(ex);
                throw new ModelManagerInitializationException(
                    "ModelManager başlatma sırasında hata oluştu", ex);
            }
            finally
            {
                if (_modelLock.IsWriteLockHeld)
                    _modelLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Model kaydeder.
        /// </summary>
        public async Task<ModelRegistrationResult> RegisterModelAsync(
            ModelMetadata metadata,
            byte[] modelData,
            Dictionary<string, byte[]> additionalFiles = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateMetadata(metadata);

            if (modelData == null || modelData.Length == 0)
                throw new ArgumentException("Model verisi boş olamaz", nameof(modelData));

            try
            {
                await ValidateModelSecurityAsync(modelData, metadata).ConfigureAwait(false);

                var modelId = GenerateModelId(metadata);

                if (_models.TryGetValue(modelId, out var existingModel))
                {
                    if (existingModel?.Metadata?.Version == metadata.Version)
                    {
                        return ModelRegistrationResult.Failed(
                            $"Model zaten kayıtlı: {modelId}",
                            ModelRegistrationError.DuplicateModel);
                    }
                }

                var container = new ModelContainer
                {
                    ModelId = modelId,
                    Metadata = metadata,
                    ModelData = modelData,
                    AdditionalFiles = additionalFiles ?? new Dictionary<string, byte[]>(),
                    RegistrationDate = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow,
                    Status = ModelStatus.Registered,
                    AccessCount = 0,
                    SizeInBytes = modelData.Length
                };

                await SaveModelToRepositoryAsync(container, cancellationToken).ConfigureAwait(false);

                _models[modelId] = container;
                _totalModelsLoaded++;

                _performanceMetrics[modelId] = new ModelPerformanceMetrics
                {
                    ModelId = modelId,
                    StartTime = DateTime.UtcNow,
                    TotalInferences = 0,
                    SuccessfulInferences = 0,
                    FailedInferences = 0,
                    AverageInferenceTime = TimeSpan.Zero,
                    MemoryUsage = container.SizeInBytes
                };

                LogModelEvent(
                    ModelEventType.ModelRegistered,
                    $"Model kaydedildi: {metadata.Name} v{metadata.Version}",
                    modelId,
                    ModelSeverity.Information,
                    metadata: metadata);

                return ModelRegistrationResult.Success(modelId, container);
            }
            catch (OperationCanceledException)
            {
                LogModelEvent(
                    ModelEventType.OperationCancelled,
                    "Model kayıt işlemi iptal edildi",
                    modelId: null,
                    severity: ModelSeverity.Warning,
                    metadata: metadata);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model kaydı sırasında hata oluştu: {ModelName}", metadata?.Name);

                LogModelEvent(
                    ModelEventType.RegistrationFailed,
                    $"Model kaydı başarısız: {ex.Message}",
                    modelId: null,
                    severity: ModelSeverity.Error,
                    metadata: metadata);

                throw new ModelRegistrationException("Model kaydı sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Modeli yükler ve hazırlar.
        /// </summary>
        public async Task<ModelLoadResult> LoadModelAsync(
            string modelId,
            ModelLoadOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            options ??= new ModelLoadOptions();

            try
            {
                if (!_models.TryGetValue(modelId, out var container))
                {
                    container = await LoadModelFromRepositoryAsync(modelId, cancellationToken).ConfigureAwait(false);

                    if (container == null)
                    {
                        return ModelLoadResult.Failed(
                            $"Model bulunamadı: {modelId}",
                            ModelLoadError.ModelNotFound);
                    }

                    _models[modelId] = container;
                }

                if (container.Status == ModelStatus.Loaded && !options.ForceReload)
                {
                    container.LastAccessed = DateTime.UtcNow;
                    container.AccessCount++;
                    return ModelLoadResult.Success(container, loadedNow: false);
                }

                if (container.Status == ModelStatus.Corrupted)
                {
                    return ModelLoadResult.Failed(
                        "Model bozuk, yeniden yüklenmesi gerekiyor",
                        ModelLoadError.ModelCorrupted);
                }

                await LoadModelInternalAsync(container, options, cancellationToken).ConfigureAwait(false);

                container.Status = ModelStatus.Loaded;
                container.LastAccessed = DateTime.UtcNow;
                container.AccessCount++;
                container.LoadedAt = DateTime.UtcNow;

                UpdateMemoryUsage();

                LogModelEvent(
                    ModelEventType.ModelLoaded,
                    $"Model yüklendi: {container.Metadata?.Name}",
                    modelId,
                    ModelSeverity.Information,
                    metadata: container.Metadata);

                return ModelLoadResult.Success(container, loadedNow: true);
            }
            catch (OperationCanceledException)
            {
                LogModelEvent(
                    ModelEventType.OperationCancelled,
                    "Model yükleme işlemi iptal edildi",
                    modelId,
                    ModelSeverity.Warning);
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model yükleme sırasında hata oluştu: {ModelId}", modelId);

                if (_models.TryGetValue(modelId, out var c))
                {
                    c.Status = ModelStatus.Corrupted;
                }

                LogModelEvent(
                    ModelEventType.LoadFailed,
                    $"Model yükleme başarısız: {ex.Message}",
                    modelId,
                    ModelSeverity.Error);

                throw new ModelLoadException("Model yükleme sırasında hata oluştu", ex);
            }
        }

        /// <summary>
        /// Modeli tahmin için kullanır.
        /// </summary>
        public async Task<ModelPredictionResult> PredictAsync<TInput, TOutput>(
            string modelId,
            TInput input,
            PredictionOptions options = null,
            CancellationToken cancellationToken = default)
            where TInput : class
            where TOutput : class, new()
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            if (input == null)
                throw new ArgumentNullException(nameof(input));

            options ??= new PredictionOptions();

            var startTime = DateTime.UtcNow;
            var metrics = _performanceMetrics.GetOrAdd(
                modelId,
                id => new ModelPerformanceMetrics { ModelId = id });

            try
            {
                var loadResult = await LoadModelAsync(
                    modelId,
                    new ModelLoadOptions { ForceReload = options.ForceReload },
                    cancellationToken).ConfigureAwait(false);

                if (!loadResult.IsSuccess)
                {
                    metrics.FailedInferences++;
                    return ModelPredictionResult.Failed(
                        $"Model yüklenemedi: {loadResult.ErrorMessage}",
                        PredictionError.ModelNotLoaded);
                }

                var container = loadResult.Container;

                await ValidateInputAsync(input, container.Metadata, cancellationToken).ConfigureAwait(false);

                var prediction = await ExecutePredictionAsync<TInput, TOutput>(
                    container,
                    input,
                    options,
                    cancellationToken).ConfigureAwait(false);

                await ValidateOutputAsync(prediction, container.Metadata, cancellationToken).ConfigureAwait(false);

                var duration = DateTime.UtcNow - startTime;
                UpdatePerformanceMetrics(metrics, duration, success: true);

                if (options.LogPrediction)
                    await LogPredictionAsync(modelId, input, prediction, duration, cancellationToken).ConfigureAwait(false);

                LogModelEvent(
                    ModelEventType.PredictionMade,
                    $"Tahmin tamamlandı: {modelId}, Süre: {duration.TotalMilliseconds}ms",
                    modelId,
                    ModelSeverity.Information,
                    metadata: container.Metadata);

                return ModelPredictionResult.Success(prediction, duration);
            }
            catch (OperationCanceledException)
            {
                metrics.FailedInferences++;
                LogModelEvent(ModelEventType.OperationCancelled, "Tahmin işlemi iptal edildi", modelId, ModelSeverity.Warning);
                throw;
            }
            catch (Exception ex)
            {
                metrics.FailedInferences++;

                _logger?.LogError(ex, "Tahmin sırasında hata oluştu: {ModelId}", modelId);

                var duration = DateTime.UtcNow - startTime;
                UpdatePerformanceMetrics(metrics, duration, success: false);

                LogModelEvent(ModelEventType.PredictionFailed, $"Tahmin başarısız: {ex.Message}", modelId, ModelSeverity.Error);

                throw new ModelPredictionException("Tahmin sırasında hata oluştu", ex);
            }
        }

        public async Task<ModelTrainingResult> StartTrainingAsync(
            ModelTrainingRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateTrainingRequest(request);

            try
            {
                var sessionId = Guid.NewGuid().ToString();

                var session = new ModelTrainingSession
                {
                    SessionId = sessionId,
                    Request = request,
                    StartTime = DateTime.UtcNow,
                    Status = TrainingStatus.Starting,
                    Progress = 0,
                    Metrics = new Dictionary<string, double>(),
                    Checkpoints = new List<ModelCheckpoint>()
                };

                _trainingSessions[sessionId] = session;

                var trainingTask = Task.Run(async () =>
                {
                    try
                    {
                        await ExecuteTrainingAsync(session, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        session.Status = TrainingStatus.Failed;
                        session.ErrorMessage = ex.Message;
                        session.EndTime = DateTime.UtcNow;

                        LogModelEvent(
                            ModelEventType.TrainingFailed,
                            $"Eğitim başarısız: {ex.Message}",
                            modelId: null,
                            severity: ModelSeverity.Error,
                            sessionId: sessionId);
                    }
                }, cancellationToken);

                session.TrainingTask = trainingTask;

                LogModelEvent(
                    ModelEventType.TrainingStarted,
                    $"Eğitim başlatıldı: {request.ModelName}",
                    modelId: null,
                    severity: ModelSeverity.Information,
                    sessionId: sessionId);

                return ModelTrainingResult.Success(sessionId, session);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Eğitim başlatma sırasında hata oluştu");

                LogModelEvent(ModelEventType.TrainingFailed, $"Eğitim başlatma başarısız: {ex.Message}", null, ModelSeverity.Error);

                throw new ModelTrainingException("Eğitim başlatma sırasında hata oluştu", ex);
            }
        }

        public async Task<ModelDeploymentResult> DeployModelAsync(
            string modelId,
            DeploymentTarget target,
            DeploymentOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            options ??= new DeploymentOptions();

            try
            {
                if (!_models.TryGetValue(modelId, out var container))
                {
                    return ModelDeploymentResult.Failed(
                        $"Model bulunamadı: {modelId}",
                        DeploymentError.ModelNotFound);
                }

                var deploymentId = Guid.NewGuid().ToString();

                var deploymentTask = new ModelDeploymentTask
                {
                    DeploymentId = deploymentId,
                    ModelId = modelId,
                    Container = container,
                    Target = target,
                    Options = options,
                    Status = DeploymentStatus.Pending,
                    CreatedAt = DateTime.UtcNow,
                    Priority = options.Priority
                };

                _deploymentQueue.Enqueue(deploymentTask);

                LogModelEvent(
                    ModelEventType.DeploymentQueued,
                    $"Dağıtım kuyruğa alındı: {modelId} -> {target}",
                    modelId,
                    ModelSeverity.Information,
                    metadata: container.Metadata,
                    deploymentId: deploymentId);

                if (options.WaitForCompletion && options.Timeout.HasValue)
                {
                    return await WaitForDeploymentAsync(deploymentId, options.Timeout.Value, cancellationToken).ConfigureAwait(false);
                }

                return ModelDeploymentResult.Queued(deploymentId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model dağıtımı sırasında hata oluştu: {ModelId}", modelId);

                LogModelEvent(ModelEventType.DeploymentFailed, $"Dağıtım başlatma başarısız: {ex.Message}", modelId, ModelSeverity.Error);

                throw new ModelDeploymentException("Model dağıtımı sırasında hata oluştu", ex);
            }
        }

        public async Task<ModelArchiveResult> ArchiveModelAsync(
            string modelId,
            ArchiveReason reason,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            try
            {
                if (!_models.TryGetValue(modelId, out var container))
                {
                    return ModelArchiveResult.Failed(
                        $"Model bulunamadı: {modelId}",
                        ArchiveError.ModelNotFound);
                }

                if (container.Status == ModelStatus.InUse)
                {
                    return ModelArchiveResult.Failed(
                        "Model kullanımda, arşivlenemez",
                        ArchiveError.ModelInUse);
                }

                await ArchiveModelInternalAsync(container, reason, cancellationToken).ConfigureAwait(false);

                _models.TryRemove(modelId, out _);
                _performanceMetrics.TryRemove(modelId, out _);

                UpdateMemoryUsage();

                LogModelEvent(
                    ModelEventType.ModelArchived,
                    $"Model arşivlendi: {container.Metadata?.Name} - Sebep: {reason}",
                    modelId,
                    ModelSeverity.Information,
                    metadata: container.Metadata);

                return ModelArchiveResult.Success(modelId);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model arşivleme sırasında hata oluştu: {ModelId}", modelId);

                LogModelEvent(ModelEventType.ArchiveFailed, $"Arşivleme başarısız: {ex.Message}", modelId, ModelSeverity.Error);

                throw new ModelArchiveException("Model arşivleme sırasında hata oluştu", ex);
            }
        }

        public async Task<ModelUpdateResult> UpdateModelMetadataAsync(
            string modelId,
            ModelMetadataUpdate update,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            if (update == null)
                throw new ArgumentNullException(nameof(update));

            try
            {
                if (!_models.TryGetValue(modelId, out var container))
                {
                    return ModelUpdateResult.Failed(
                        $"Model bulunamadı: {modelId}",
                        ModelUpdateError.ModelNotFound);
                }

                var oldMetadata = container.Metadata?.Clone();
                container.Metadata = update.ApplyTo(container.Metadata);
                container.LastModified = DateTime.UtcNow;

                await UpdateModelInRepositoryAsync(container, cancellationToken).ConfigureAwait(false);

                LogModelEvent(
                    ModelEventType.MetadataUpdated,
                    $"Model metadata güncellendi: {modelId}",
                    modelId,
                    ModelSeverity.Information,
                    metadata: container.Metadata);

                return ModelUpdateResult.Success(modelId, oldMetadata, container.Metadata);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model metadata güncelleme sırasında hata oluştu: {ModelId}", modelId);

                LogModelEvent(ModelEventType.UpdateFailed, $"Metadata güncelleme başarısız: {ex.Message}", modelId, ModelSeverity.Error);

                throw new ModelUpdateException("Model metadata güncelleme sırasında hata oluştu", ex);
            }
        }

        public async Task<ModelAnalysisResult> AnalyzeModelPerformanceAsync(
            string modelId,
            AnalysisOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            try
            {
                if (!_performanceMetrics.TryGetValue(modelId, out var metrics))
                {
                    return ModelAnalysisResult.Failed(
                        $"Model performans metriği bulunamadı: {modelId}",
                        AnalysisError.NoMetrics);
                }

                if (!_models.TryGetValue(modelId, out var container))
                {
                    return ModelAnalysisResult.Failed(
                        $"Model bulunamadı: {modelId}",
                        AnalysisError.ModelNotFound);
                }

                var analysis = await PerformModelAnalysisAsync(container, metrics, options, cancellationToken).ConfigureAwait(false);

                LogModelEvent(
                    ModelEventType.AnalysisCompleted,
                    $"Model analizi tamamlandı: {modelId}",
                    modelId,
                    ModelSeverity.Information,
                    metadata: container.Metadata);

                return ModelAnalysisResult.Success(analysis);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model analizi sırasında hata oluştu: {ModelId}", modelId);

                LogModelEvent(ModelEventType.AnalysisFailed, $"Analiz başarısız: {ex.Message}", modelId, ModelSeverity.Error);

                throw new ModelAnalysisException("Model analizi sırasında hata oluştu", ex);
            }
        }

        public async Task<ModelSearchResult> SearchModelsAsync(
            ModelSearchCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ValidateDisposed();

            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            try
            {
                var results = new List<ModelSearchItem>();

                foreach (var container in _models.Values)
                {
                    if (await MatchesCriteriaAsync(container, criteria, cancellationToken).ConfigureAwait(false))
                    {
                        _performanceMetrics.TryGetValue(container.ModelId, out var metrics);

                        results.Add(new ModelSearchItem
                        {
                            ModelId = container.ModelId,
                            Metadata = container.Metadata,
                            Status = container.Status,
                            Performance = metrics,
                            LastAccessed = container.LastAccessed,
                            SizeInBytes = container.SizeInBytes
                        });
                    }
                }

                var repositoryResults = await SearchRepositoryAsync(criteria, cancellationToken).ConfigureAwait(false);
                results.AddRange(repositoryResults);

                results = criteria.SortBy switch
                {
                    ModelSortBy.Name => results.OrderBy(r => r.Metadata?.Name).ToList(),
                    ModelSortBy.Date => results.OrderByDescending(r => r.Metadata?.CreatedDate).ToList(),
                    ModelSortBy.Size => results.OrderByDescending(r => r.SizeInBytes).ToList(),
                    ModelSortBy.Performance => results.OrderByDescending(r => r.Performance?.SuccessRate ?? 0).ToList(),
                    _ => results.OrderByDescending(r => r.LastAccessed).ToList()
                };

                if (criteria.PageSize > 0)
                {
                    results = results
                        .Skip(criteria.PageIndex * criteria.PageSize)
                        .Take(criteria.PageSize)
                        .ToList();
                }

                return ModelSearchResult.Success(results, results.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model arama sırasında hata oluştu");
                throw new ModelSearchException("Model arama sırasında hata oluştu", ex);
            }
        }

        public ModelManagerStatus GetStatus()
        {
            ValidateDisposed();

            return new ModelManagerStatus
            {
                IsInitialized = _isInitialized,
                ActiveModelCount = ActiveModelCount,
                TotalModelsLoaded = _totalModelsLoaded,
                PendingDeployments = PendingDeployments,
                ActiveTrainingSessions = _trainingSessions.Count,
                TotalMemoryUsage = TotalMemoryUsage,
                RepositoryStatus = RepositoryStatus,
                IsAutoUpdateEnabled = IsAutoUpdateEnabled,
                LastHealthCheck = _lastHealthCheck,
                PerformanceMetrics = GetAggregatedMetrics()
            };
        }

        public bool UnloadModel(string modelId, bool force = false)
        {
            ValidateDisposed();
            ValidateModelId(modelId);

            try
            {
                if (_models.TryGetValue(modelId, out var container))
                {
                    if (container.Status == ModelStatus.InUse && !force)
                        return false;

                    UnloadModelInternal(container);

                    container.Status = ModelStatus.Unloaded;
                    container.LoadedAt = null;

                    UpdateMemoryUsage();

                    LogModelEvent(ModelEventType.ModelUnloaded, $"Model boşaltıldı: {container.Metadata?.Name}", modelId,
                        ModelSeverity.Information, metadata: container.Metadata);

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model boşaltma sırasında hata oluştu: {ModelId}", modelId);
                return false;
            }
        }

        public async Task<int> ClearAllModelsAsync(bool includeArchived = false)
        {
            ValidateDisposed();

            int count = 0;
            var modelsToRemove = _models.Keys.ToList();

            foreach (var modelId in modelsToRemove)
            {
                if (UnloadModel(modelId, true))
                {
                    _models.TryRemove(modelId, out _);
                    count++;
                }
            }

            if (includeArchived)
                await ClearArchivedModelsAsync().ConfigureAwait(false);

            _performanceMetrics.Clear();
            TotalMemoryUsage = 0;

            LogModelEvent(
                ModelEventType.AllModelsCleared,
                $"Tüm modeller temizlendi: {count} model",
                modelId: null,
                severity: ModelSeverity.Warning);

            return count;
        }

        public void SetAutoUpdate(bool enabled)
        {
            ValidateDisposed();

            IsAutoUpdateEnabled = enabled;

            LogModelEvent(
                ModelEventType.AutoUpdateChanged,
                $"Otomatik güncelleme {(enabled ? "aktif" : "pasif")} edildi",
                modelId: null,
                severity: ModelSeverity.Information);
        }
        #endregion

        #region Private Methods

        private void LoadDependencies()
        {
            var serviceProvider = ServiceProviderFactory.GetServiceProvider();

            _appConfig = serviceProvider.GetRequiredService<AppConfig>();
            _environmentConfig = serviceProvider.GetRequiredService<IEnvironmentConfig>();

            // ILoggerFactory üzerinden logger üret
            var loggerFactory = serviceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>();
            _logger = loggerFactory.CreateLogger<ModelManager>();

            _securityManager = serviceProvider.GetRequiredService<ISecurityManager>();
            _recoveryEngine = serviceProvider.GetRequiredService<IRecoveryEngine>();
        }

        private void ConfigurePaths()
        {
            _modelRepositoryPath = Path.Combine(
                _appConfig.DataPaths.ModelRepository,
                _environmentConfig.EnvironmentName);

            _cachePath = Path.Combine(
                _appConfig.DataPaths.Cache,
                "models",
                _environmentConfig.EnvironmentName);

            _deploymentPath = Path.Combine(
                _appConfig.DataPaths.Deployments,
                _environmentConfig.EnvironmentName);

            Directory.CreateDirectory(_modelRepositoryPath);
            Directory.CreateDirectory(_cachePath);
            Directory.CreateDirectory(_deploymentPath);
        }

        private void InitializeRepository()
        {
            try
            {
                var metadataPath = Path.Combine(_modelRepositoryPath, "repository.json");

                if (File.Exists(metadataPath))
                {
                    var json = File.ReadAllText(metadataPath);
                    RepositoryStatus = JsonConvert.DeserializeObject<ModelRepositoryStatus>(json);
                }
                else
                {
                    RepositoryStatus = new ModelRepositoryStatus
                    {
                        RepositoryId = Guid.NewGuid().ToString(),
                        CreatedAt = DateTime.UtcNow,
                        LastMaintenance = DateTime.UtcNow,
                        TotalModels = 0,
                        TotalSize = 0,
                        IsEncrypted = _appConfig.Security.EncryptModelStorage
                    };

                    SaveRepositoryStatus();
                }

                // Basit bütünlük kontrolü
                VerifyRepositoryIntegrity();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Model repository başlatma sırasında hata oluştu");
                throw;
            }
        }

        private void SaveRepositoryStatus()
        {
            var metadataPath = Path.Combine(_modelRepositoryPath, "repository.json");
            var json = JsonConvert.SerializeObject(RepositoryStatus, Formatting.Indented);
            File.WriteAllText(metadataPath, json);
        }

        private void ClearCache()
        {
            try
            {
                if (!Directory.Exists(_cachePath))
                    return;

                var cacheFiles = Directory.GetFiles(_cachePath, "*.cache");
                foreach (var file in cacheFiles)
                {
                    try { File.Delete(file); }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache temizleme sırasında hata oluştu");
            }
        }

        private void LoadModels()
        {
            try
            {
                // repository içinde *.model.json metadata dosyalarını tara
                var metadataFiles = Directory.GetFiles(_modelRepositoryPath, "*.model.json");
                foreach (var metadataFile in metadataFiles)
                {
                    try
                    {
                        var json = File.ReadAllText(metadataFile);
                        var metadata = JsonConvert.DeserializeObject<ModelMetadata>(json);
                        if (metadata == null) continue;

                        var modelId = GenerateModelId(metadata);
                        var modelBinPath = metadataFile.Replace(".model.json", ".bin");

                        if (!File.Exists(modelBinPath))
                            continue;

                        var modelData = File.ReadAllBytes(modelBinPath);

                        var container = new ModelContainer
                        {
                            ModelId = modelId,
                            Metadata = metadata,
                            ModelData = modelData,
                            RegistrationDate = File.GetCreationTimeUtc(modelBinPath),
                            LastAccessed = DateTime.UtcNow,
                            Status = ModelStatus.Registered,
                            SizeInBytes = modelData.Length
                        };

                        _models[modelId] = container;
                        _totalModelsLoaded++;

                        _performanceMetrics[modelId] = new ModelPerformanceMetrics
                        {
                            ModelId = modelId,
                            StartTime = DateTime.UtcNow
                        };
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Model yükleme sırasında hata: {File}", metadataFile);
                    }
                }

                _logger?.LogInformation("{Count} model repository'den yüklendi", _models.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Modeller yüklenirken hata oluştu");
                throw;
            }
        }

        private void StartDeploymentTask()
        {
            _deploymentTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        if (_deploymentQueue.TryDequeue(out var task))
                        {
                            await ProcessDeploymentAsync(task).ConfigureAwait(false);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromSeconds(5), _cancellationTokenSource.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Dağıtım görevi sırasında hata oluştu");
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), _cancellationTokenSource.Token).ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private void StartMonitoringTask()
        {
            _monitoringTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await MonitorModelsAsync().ConfigureAwait(false);
                        await CheckForUpdatesAsync().ConfigureAwait(false);
                        await PerformHealthCheckAsync().ConfigureAwait(false);

                        await Task.Delay(TimeSpan.FromMinutes(1), _cancellationTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "İzleme görevi sırasında hata oluştu");
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token).ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private void StartCleanupTask()
        {
            _cleanupTask = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.IsCancellationRequested)
                {
                    try
                    {
                        await CleanupExpiredModelsAsync().ConfigureAwait(false);
                        await CleanupOldCacheAsync().ConfigureAwait(false);
                        await CleanupCompletedTrainingsAsync().ConfigureAwait(false);

                        await Task.Delay(TimeSpan.FromMinutes(5), _cancellationTokenSource.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Temizleme görevi sırasında hata oluştu");
                        try
                        {
                            await Task.Delay(TimeSpan.FromMinutes(1), _cancellationTokenSource.Token).ConfigureAwait(false);
                        }
                        catch { /* ignore */ }
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        private async Task ValidateModelSecurityAsync(byte[] modelData, ModelMetadata metadata)
        {
            if (modelData == null) throw new ArgumentNullException(nameof(modelData));

            // boyut kontrolü
            if (modelData.Length > _appConfig.ModelManagement.MaxModelSize)
            {
                throw new ModelSecurityException(
                    $"Model boyutu çok büyük: {modelData.Length} bytes (Max: {_appConfig.ModelManagement.MaxModelSize})");
            }

            // basit imza kontrolü
            await CheckForMaliciousModelAsync(modelData, metadata).ConfigureAwait(false);

            // şifreleme gerekiyorsa (Not: bu metod modelData'yı çağıranın kullandığı buffer'a geri vermez,
            // gerçek kaydetme aşamasında container.ModelData üzerinde uygulanması gerekir.)
            // Burada sadece güvenlik adımı olarak bırakıyoruz.
        }

        private async Task CheckForMaliciousModelAsync(byte[] modelData, ModelMetadata metadata)
        {
            // Basit imza kontrolü (gerçekte anti-malware vs)
            var suspiciousPatterns = new[]
            {
                new byte[] { 0x4D, 0x5A },             // MZ
                new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, // ELF
                new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }  // Java class
            };

            foreach (var pattern in suspiciousPatterns)
            {
                if (ContainsPattern(modelData, pattern))
                {
                    throw new ModelSecurityException(
                        "Model şüpheli içerik tespit edildi. Güvenlik nedeniyle reddedildi.");
                }
            }

            await Task.CompletedTask;
        }

        private bool ContainsPattern(byte[] data, byte[] pattern)
        {
            if (data == null || pattern == null || pattern.Length == 0) return false;
            if (data.Length < pattern.Length) return false;

            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return true;
            }
            return false;
        }

        private string GenerateModelId(ModelMetadata metadata)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            var input = $"{metadata.Name}:{metadata.Version}:{metadata.Framework}:{metadata.CreatedDate:O}";
            var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 32).ToLowerInvariant();
        }

        private async Task SaveModelToRepositoryAsync(ModelContainer container, CancellationToken cancellationToken)
        {
            var modelId = container.ModelId;

            var modelPath = Path.Combine(_modelRepositoryPath, $"{modelId}.bin");
            var metadataPath = Path.Combine(_modelRepositoryPath, $"{modelId}.model.json");

            byte[] dataToWrite = container.ModelData;

            // Şifreleme
            if (_appConfig.Security.EncryptModelStorage)
            {
                dataToWrite = await _securityManager.EncryptAsync(dataToWrite, "model_storage_key").ConfigureAwait(false);
            }

            await File.WriteAllBytesAsync(modelPath, dataToWrite, cancellationToken).ConfigureAwait(false);

            // Metadata (hash vb. alanlar varsa burada güncellenebilir)
            var metadataJson = JsonConvert.SerializeObject(container.Metadata, Formatting.Indented);
            await File.WriteAllTextAsync(metadataPath, metadataJson, cancellationToken).ConfigureAwait(false);

            RepositoryStatus.TotalModels++;
            RepositoryStatus.TotalSize += container.SizeInBytes;
            RepositoryStatus.LastUpdated = DateTime.UtcNow;
            SaveRepositoryStatus();
        }

        private async Task<ModelContainer> LoadModelFromRepositoryAsync(string modelId, CancellationToken cancellationToken)
        {
            var modelPath = Path.Combine(_modelRepositoryPath, $"{modelId}.bin");
            var metadataPath = Path.Combine(_modelRepositoryPath, $"{modelId}.model.json");

            if (!File.Exists(modelPath) || !File.Exists(metadataPath))
                return null;

            var metadataJson = await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false);
            var metadata = JsonConvert.DeserializeObject<ModelMetadata>(metadataJson);
            if (metadata == null) return null;

            var modelData = await File.ReadAllBytesAsync(modelPath, cancellationToken).ConfigureAwait(false);

            if (_appConfig.Security.EncryptModelStorage)
            {
                modelData = await _securityManager.DecryptAsync(modelData, "model_storage_key").ConfigureAwait(false);
            }

            return new ModelContainer
            {
                ModelId = modelId,
                Metadata = metadata,
                ModelData = modelData,
                RegistrationDate = File.GetCreationTimeUtc(modelPath),
                LastAccessed = DateTime.UtcNow,
                Status = ModelStatus.Registered,
                SizeInBytes = modelData.Length
            };
        }

        private async Task LoadModelInternalAsync(
            ModelContainer container,
            ModelLoadOptions options,
            CancellationToken cancellationToken)
        {
            var fw = (container.Metadata?.Framework ?? string.Empty).ToLowerInvariant();

            switch (fw)
            {
                case "tensorflow":
                case "tf":
                    await LoadTensorFlowModelAsync(container, options, cancellationToken).ConfigureAwait(false);
                    break;

                case "pytorch":
                case "torch":
                    await LoadPyTorchModelAsync(container, options, cancellationToken).ConfigureAwait(false);
                    break;

                case "onnx":
                    await LoadOnnxModelAsync(container, options, cancellationToken).ConfigureAwait(false);
                    break;

                case "ml.net":
                case "mlnet":
                    await LoadMlNetModelAsync(container, options, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new NotSupportedException($"Framework desteklenmiyor: {container.Metadata?.Framework}");
            }

            if (options.CacheModel)
                await CacheModelAsync(container, cancellationToken).ConfigureAwait(false);
        }

        private async Task LoadTensorFlowModelAsync(
            ModelContainer container,
            ModelLoadOptions options,
            CancellationToken cancellationToken)
        {
            // TensorFlow graph import
            var graph = new TFGraph();
            graph.Import(container.ModelData);

            container.SessionData = new TFSession(graph);

            if (options.UseGpu && HasGpu())
            {
                container.SessionOptions = new TFSessionOptions();
            }

            await Task.CompletedTask;
        }

        private async Task LoadPyTorchModelAsync(
            ModelContainer container,
            ModelLoadOptions options,
            CancellationToken cancellationToken)
        {
            // TorchSharp ile placeholder
            using var stream = new MemoryStream(container.ModelData);

            // gerçek implementasyonda torch.jit.load vs.
            var device = (options.UseGpu && HasGpu()) ? torch.CUDA : torch.CPU;
            _ = device;

            container.SessionData = new object();

            await Task.CompletedTask;
        }

        private async Task LoadOnnxModelAsync(
            ModelContainer container,
            ModelLoadOptions options,
            CancellationToken cancellationToken)
        {
            // Placeholder: OnnxRuntime
            using var stream = new MemoryStream(container.ModelData);
            _ = stream;

            await Task.CompletedTask;
        }

        private async Task LoadMlNetModelAsync(
            ModelContainer container,
            ModelLoadOptions options,
            CancellationToken cancellationToken)
        {
            using var stream = new MemoryStream(container.ModelData);
            var context = new MLContext();
            container.SessionData = context.Model.Load(stream, out _);

            await Task.CompletedTask;
        }

        private async Task CacheModelAsync(ModelContainer container, CancellationToken cancellationToken)
        {
            var cacheFile = Path.Combine(_cachePath, $"{container.ModelId}.cache");
            await File.WriteAllBytesAsync(cacheFile, container.ModelData, cancellationToken).ConfigureAwait(false);
        }

        private bool HasGpu()
        {
            // Basit kontrol (placeholder)
            return Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") != null;
        }

        private bool VerifyRepositoryIntegrity()
        {
            try
            {
                var metadataPath = Path.Combine(_modelRepositoryPath, "repository.json");
                if (!File.Exists(metadataPath)) return false;

                var modelFiles = Directory.GetFiles(_modelRepositoryPath, "*.bin");
                var expectedCount = RepositoryStatus?.TotalModels ?? 0;

                return modelFiles.Length >= expectedCount;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Prediction + Metrics + Logging + Deployment Helpers

        private async Task ValidateInputAsync<TInput>(
            TInput input,
            ModelMetadata metadata,
            CancellationToken cancellationToken)
        {
            if (input == null)
                throw new ArgumentNullException(nameof(input));

            // Metadata'da giriş şeması varsa kontrol
            if (metadata?.InputSchema != null && metadata.InputSchema.Count > 0)
            {
                await ValidateAgainstSchemaAsync(input, metadata.InputSchema, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ValidateOutputAsync<TOutput>(
            TOutput output,
            ModelMetadata metadata,
            CancellationToken cancellationToken)
        {
            if (output == null)
                throw new ModelValidationException("Model çıktısı null");

            if (metadata?.OutputSchema != null && metadata.OutputSchema.Count > 0)
            {
                await ValidateAgainstSchemaAsync(output, metadata.OutputSchema, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ValidateAgainstSchemaAsync(
            object data,
            Dictionary<string, Type> schema,
            CancellationToken cancellationToken)
        {
            // Placeholder doğrulama
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task<TOutput> ExecutePredictionAsync<TInput, TOutput>(
            ModelContainer container,
            TInput input,
            PredictionOptions options,
            CancellationToken cancellationToken)
            where TInput : class
            where TOutput : class, new()
        {
            var fw = (container.Metadata?.Framework ?? string.Empty).ToLowerInvariant();

            switch (fw)
            {
                case "tensorflow":
                case "tf":
                    return await PredictTensorFlowAsync<TInput, TOutput>(container, input, options, cancellationToken)
                        .ConfigureAwait(false);

                case "pytorch":
                case "torch":
                    return await PredictPyTorchAsync<TInput, TOutput>(container, input, options, cancellationToken)
                        .ConfigureAwait(false);

                case "onnx":
                    return await PredictOnnxAsync<TInput, TOutput>(container, input, options, cancellationToken)
                        .ConfigureAwait(false);

                case "ml.net":
                case "mlnet":
                    return await PredictMlNetAsync<TInput, TOutput>(container, input, options, cancellationToken)
                        .ConfigureAwait(false);

                default:
                    throw new NotSupportedException($"Framework desteklenmiyor: {container.Metadata?.Framework}");
            }
        }

        private async Task<TOutput> PredictTensorFlowAsync<TInput, TOutput>(
            ModelContainer container,
            TInput input,
            PredictionOptions options,
            CancellationToken cancellationToken)
            where TOutput : class, new()
        {
            // TensorFlow placeholder tahmin
            if (container.SessionData is not TFSession session)
                throw new InvalidOperationException("TensorFlow session bulunamadı");

            // (Gerçek implementasyonda input/output isimleri metadata'dan yönetilir.)
            var inputTensor = ConvertToTensor(input, container.Metadata);

            // Output isimleri
            var outputNames = container.Metadata?.Outputs?.ToArray() ?? new[] { "output" };

            var outputTensors = new TFOutput[outputNames.Length];
            for (int i = 0; i < outputNames.Length; i++)
            {
                // Graph erişimi placeholder
                outputTensors[i] = session.Graph[outputNames[i]];
            }

            // Input node adı placeholder: "input"
            var results = session.Run(
                new[] { inputTensor },
                new[] { session.Graph["input"] },
                outputTensors);

            var output = ConvertFromTensor<TOutput>(results[0], container.Metadata);

            await Task.CompletedTask.ConfigureAwait(false);
            return output;
        }

        private async Task<TOutput> PredictPyTorchAsync<TInput, TOutput>(
            ModelContainer container,
            TInput input,
            PredictionOptions options,
            CancellationToken cancellationToken)
            where TOutput : class, new()
        {
            // Placeholder
            await Task.CompletedTask.ConfigureAwait(false);
            return new TOutput();
        }

        private async Task<TOutput> PredictOnnxAsync<TInput, TOutput>(
            ModelContainer container,
            TInput input,
            PredictionOptions options,
            CancellationToken cancellationToken)
            where TOutput : class, new()
        {
            // Placeholder
            await Task.CompletedTask.ConfigureAwait(false);
            return new TOutput();
        }

        private async Task<TOutput> PredictMlNetAsync<TInput, TOutput>(
            ModelContainer container,
            TInput input,
            PredictionOptions options,
            CancellationToken cancellationToken)
            where TOutput : class, new()
        {
            // Placeholder
            await Task.CompletedTask.ConfigureAwait(false);
            return new TOutput();
        }

        private TFTensor ConvertToTensor<TInput>(TInput input, ModelMetadata metadata)
        {
            // Placeholder
            return new TFTensor(0);
        }

        private TOutput ConvertFromTensor<TOutput>(TFTensor tensor, ModelMetadata metadata)
            where TOutput : class, new()
        {
            // Placeholder
            return new TOutput();
        }

        private void UpdatePerformanceMetrics(
            ModelPerformanceMetrics metrics,
            TimeSpan duration,
            bool success)
        {
            metrics.TotalInferences++;

            if (success)
            {
                metrics.SuccessfulInferences++;

                if (metrics.AverageInferenceTime == TimeSpan.Zero)
                {
                    metrics.AverageInferenceTime = duration;
                }
                else
                {
                    var prevMs = metrics.AverageInferenceTime.TotalMilliseconds;
                    var n = Math.Max(1, metrics.SuccessfulInferences);

                    metrics.AverageInferenceTime = TimeSpan.FromMilliseconds(
                        (prevMs * (n - 1) + duration.TotalMilliseconds) / n);
                }
            }
            else
            {
                metrics.FailedInferences++;
            }

            metrics.SuccessRate = metrics.TotalInferences > 0
                ? (double)metrics.SuccessfulInferences / metrics.TotalInferences
                : 0;
        }

        private async Task LogPredictionAsync<TInput, TOutput>(
            string modelId,
            TInput input,
            TOutput output,
            TimeSpan duration,
            CancellationToken cancellationToken)
        {
            var logEntry = new PredictionLogEntry
            {
                Id = Guid.NewGuid().ToString(),
                ModelId = modelId,
                Timestamp = DateTime.UtcNow,
                Input = JsonConvert.SerializeObject(input),
                Output = JsonConvert.SerializeObject(output),
                DurationMs = duration.TotalMilliseconds,
                Success = true
            };

            var logDir = Path.Combine(_modelRepositoryPath, "logs");
            Directory.CreateDirectory(logDir);

            var logPath = Path.Combine(logDir, $"{DateTime.UtcNow:yyyy-MM-dd}.json");
            var logLine = JsonConvert.SerializeObject(logEntry) + Environment.NewLine;

            await File.AppendAllTextAsync(logPath, logLine, cancellationToken).ConfigureAwait(false);
        }

        private AggregatedMetrics GetAggregatedMetrics()
        {
            var list = _performanceMetrics.Values.ToList();
            if (list.Count == 0)
            {
                return new AggregatedMetrics
                {
                    TotalModels = _models.Count,
                    TotalInferences = 0,
                    AverageSuccessRate = 0,
                    AverageInferenceTime = TimeSpan.Zero,
                    TotalMemoryUsage = TotalMemoryUsage
                };
            }

            var avgMs = list.Where(m => m.AverageInferenceTime != TimeSpan.Zero)
                            .Select(m => m.AverageInferenceTime.TotalMilliseconds)
                            .DefaultIfEmpty(0)
                            .Average();

            return new AggregatedMetrics
            {
                TotalModels = _models.Count,
                TotalInferences = list.Sum(m => m.TotalInferences),
                AverageSuccessRate = list.Average(m => m.SuccessRate),
                AverageInferenceTime = TimeSpan.FromMilliseconds(avgMs),
                TotalMemoryUsage = TotalMemoryUsage
            };
        }

        private void UpdateMemoryUsage()
        {
            TotalMemoryUsage = _models.Values
                .Where(m => m.Status == ModelStatus.Loaded)
                .Sum(m => m.SizeInBytes);
        }

        private async Task ProcessDeploymentAsync(ModelDeploymentTask task)
        {
            await _deploymentLock.WaitAsync(_cancellationTokenSource.Token).ConfigureAwait(false);
            try
            {
                task.Status = DeploymentStatus.InProgress;
                task.StartedAt = DateTime.UtcNow;

                switch (task.Target)
                {
                    case DeploymentTarget.WebService:
                        await DeployToWebServiceAsync(task).ConfigureAwait(false);
                        break;

                    case DeploymentTarget.EdgeDevice:
                        await DeployToEdgeDeviceAsync(task).ConfigureAwait(false);
                        break;

                    case DeploymentTarget.Mobile:
                        await DeployToMobileAsync(task).ConfigureAwait(false);
                        break;

                    case DeploymentTarget.Cloud:
                        await DeployToCloudAsync(task).ConfigureAwait(false);
                        break;

                    default:
                        // no-op
                        break;
                }

                task.Status = DeploymentStatus.Completed;
                task.CompletedAt = DateTime.UtcNow;

                LogModelEvent(
                    ModelEventType.DeploymentCompleted,
                    $"Dağıtım tamamlandı: {task.ModelId} -> {task.Target}",
                    task.ModelId,
                    ModelSeverity.Information,
                    metadata: task.Container?.Metadata,
                    deploymentId: task.DeploymentId);
            }
            catch (Exception ex)
            {
                task.Status = DeploymentStatus.Failed;
                task.ErrorMessage = ex.Message;
                task.CompletedAt = DateTime.UtcNow;

                LogModelEvent(
                    ModelEventType.DeploymentFailed,
                    $"Dağıtım başarısız: {ex.Message}",
                    task.ModelId,
                    ModelSeverity.Error,
                    metadata: task.Container?.Metadata,
                    deploymentId: task.DeploymentId);

                throw;
            }
            finally
            {
                _deploymentLock.Release();
            }
        }

        // ---- Deployment targets (placeholder) ----
        private Task DeployToWebServiceAsync(ModelDeploymentTask task) => Task.CompletedTask;
        private Task DeployToEdgeDeviceAsync(ModelDeploymentTask task) => Task.CompletedTask;
        private Task DeployToMobileAsync(ModelDeploymentTask task) => Task.CompletedTask;
        private Task DeployToCloudAsync(ModelDeploymentTask task) => Task.CompletedTask;

        private async Task<ModelDeploymentResult> WaitForDeploymentAsync(
            string deploymentId,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var start = DateTime.UtcNow;

            while (DateTime.UtcNow - start < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Queue üzerinde arama (placeholder mantık)
                var found = _deploymentQueue.FirstOrDefault(d => d.DeploymentId == deploymentId);

                if (found == null)
                {
                    // tamamlanmış/çıkarılmış olabilir
                    break;
                }

                if (found.Status == DeploymentStatus.Completed)
                    return ModelDeploymentResult.Success(deploymentId);

                if (found.Status == DeploymentStatus.Failed)
                {
                    return ModelDeploymentResult.Failed(
                        found.ErrorMessage ?? "Dağıtım başarısız",
                        DeploymentError.DeploymentFailed);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }

            return ModelDeploymentResult.Failed(
                "Dağıtım zaman aşımına uğradı",
                DeploymentError.Timeout);
        }

        private void LogModelEvent(
            ModelEventType eventType,
            string message,
            string modelId,
            ModelSeverity severity,
            ModelMetadata metadata = null,
            string sessionId = null,
            string deploymentId = null)
        {
            var args = new ModelEventArgs
            {
                EventType = eventType,
                ModelId = modelId,
                Message = message,
                Severity = severity,
                Timestamp = DateTime.UtcNow,
                Metadata = metadata,
                SessionId = sessionId,
                DeploymentId = deploymentId,
                AdditionalData = new Dictionary<string, object>()
            };

            OnModelEvent(args);

            // Log'a da yaz
            var logLevel = severity switch
            {
                ModelSeverity.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
                ModelSeverity.Information => Microsoft.Extensions.Logging.LogLevel.Information,
                ModelSeverity.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
                ModelSeverity.Error => Microsoft.Extensions.Logging.LogLevel.Error,
                ModelSeverity.Critical => Microsoft.Extensions.Logging.LogLevel.Critical,
                _ => Microsoft.Extensions.Logging.LogLevel.Information
            };

            _logger?.Log(logLevel, "[MODEL] {EventType}: {Message}", eventType, message);
        }

        private void OnModelEvent(ModelEventArgs e)
        {
            ModelEvent?.Invoke(this, e);
        }

        #endregion

        #region Training + Archive + Monitor + Health + Cleanup + Validation + Dispose

        private async Task ExecuteTrainingAsync(
            ModelTrainingSession session,
            CancellationToken cancellationToken)
        {
            session.Status = TrainingStatus.Running;

            try
            {
                // Placeholder pipeline
                var pipeline = CreateTrainingPipeline(session.Request);

                var trainingData = await LoadTrainingDataAsync(session.Request, cancellationToken).ConfigureAwait(false);

                for (int epoch = 0; epoch < session.Request.Epochs; epoch++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var epochMetrics = await TrainEpochAsync(
                        pipeline,
                        trainingData,
                        epoch,
                        cancellationToken).ConfigureAwait(false);

                    session.Metrics[$"epoch_{epoch}"] = epochMetrics.Loss;
                    session.Progress = (int)((epoch + 1) * 100.0 / session.Request.Epochs);

                    if (session.Request.CheckpointInterval > 0 &&
                        (epoch + 1) % session.Request.CheckpointInterval == 0)
                    {
                        var checkpoint = await CreateCheckpointAsync(
                            pipeline,
                            epoch,
                            epochMetrics,
                            cancellationToken).ConfigureAwait(false);

                        session.Checkpoints.Add(checkpoint);
                    }

                    OnModelEvent(new ModelEventArgs
                    {
                        EventType = ModelEventType.TrainingProgress,
                        ModelId = null,
                        Message = $"Epoch {epoch + 1}/{session.Request.Epochs} tamamlandı",
                        Severity = ModelSeverity.Information,
                        SessionId = session.SessionId,
                        Metadata = null
                    });
                }

                var trainedModel = await SaveTrainedModelAsync(pipeline, session.Request, cancellationToken).ConfigureAwait(false);

                session.Status = TrainingStatus.Completed;
                session.EndTime = DateTime.UtcNow;
                session.TrainedModel = trainedModel;

                LogModelEvent(
                    ModelEventType.TrainingCompleted,
                    $"Eğitim tamamlandı: {session.Request.ModelName}",
                    modelId: null,
                    severity: ModelSeverity.Information,
                    sessionId: session.SessionId);
            }
            catch (OperationCanceledException)
            {
                session.Status = TrainingStatus.Cancelled;
                session.EndTime = DateTime.UtcNow;
                throw;
            }
        }

        // ---- Training helpers (placeholder) ----
        private object CreateTrainingPipeline(ModelTrainingRequest request) => new object();
        private Task<object> LoadTrainingDataAsync(ModelTrainingRequest request, CancellationToken ct) => Task.FromResult<object>(new object());

        private Task<EpochMetrics> TrainEpochAsync(object pipeline, object data, int epoch, CancellationToken ct)
            => Task.FromResult(new EpochMetrics { Loss = 0.0 });

        private Task<ModelCheckpoint> CreateCheckpointAsync(object pipeline, int epoch, EpochMetrics metrics, CancellationToken ct)
            => Task.FromResult(new ModelCheckpoint());

        private Task<object> SaveTrainedModelAsync(object pipeline, ModelTrainingRequest request, CancellationToken ct)
            => Task.FromResult<object>(new object());

        private async Task ArchiveModelInternalAsync(
            ModelContainer container,
            ArchiveReason reason,
            CancellationToken cancellationToken)
        {
            var archiveDir = Path.Combine(_modelRepositoryPath, "archive");
            Directory.CreateDirectory(archiveDir);

            var archiveId = $"{container.ModelId}_{DateTime.UtcNow:yyyyMMddHHmmss}";
            var archiveFile = Path.Combine(archiveDir, $"{archiveId}.zip");

            // Placeholder zip stream
            using var archiveStream = new MemoryStream();
            await File.WriteAllBytesAsync(archiveFile, archiveStream.ToArray(), cancellationToken).ConfigureAwait(false);

            // Repo'dan sil
            var modelFile = Path.Combine(_modelRepositoryPath, $"{container.ModelId}.bin");
            var metadataFile = Path.Combine(_modelRepositoryPath, $"{container.ModelId}.model.json");

            try { if (File.Exists(modelFile)) File.Delete(modelFile); } catch { /* ignore */ }
            try { if (File.Exists(metadataFile)) File.Delete(metadataFile); } catch { /* ignore */ }

            if (RepositoryStatus != null)
            {
                RepositoryStatus.TotalModels = Math.Max(0, RepositoryStatus.TotalModels - 1);
                RepositoryStatus.TotalSize = Math.Max(0, RepositoryStatus.TotalSize - container.SizeInBytes);
                RepositoryStatus.LastUpdated = DateTime.UtcNow;
                SaveRepositoryStatus();
            }
        }

        private async Task UpdateModelInRepositoryAsync(
            ModelContainer container,
            CancellationToken cancellationToken)
        {
            var metadataPath = Path.Combine(_modelRepositoryPath, $"{container.ModelId}.model.json");
            var metadataJson = JsonConvert.SerializeObject(container.Metadata, Formatting.Indented);
            await File.WriteAllTextAsync(metadataPath, metadataJson, cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<ModelSearchItem>> SearchRepositoryAsync(
            ModelSearchCriteria criteria,
            CancellationToken cancellationToken)
        {
            var results = new List<ModelSearchItem>();

            var archivePath = Path.Combine(_modelRepositoryPath, "archive");
            if (!Directory.Exists(archivePath))
                return results;

            var archiveFiles = Directory.GetFiles(archivePath, "*.zip");
            foreach (var file in archiveFiles)
            {
                try
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    var parts = fileName.Split('_');

                    if (parts.Length < 1) continue;

                    var modelId = parts[0];

                    // Cache'te yoksa listele
                    if (!_models.ContainsKey(modelId))
                    {
                        var item = new ModelSearchItem
                        {
                            ModelId = modelId,
                            Status = ModelStatus.Archived,
                            LastAccessed = File.GetLastAccessTimeUtc(file),
                            SizeInBytes = new FileInfo(file).Length
                        };

                        if (MatchesCriteria(item, criteria))
                            results.Add(item);
                    }
                }
                catch { /* ignore */ }
            }

            await Task.CompletedTask.ConfigureAwait(false);
            return results;
        }

        private async Task<bool> MatchesCriteriaAsync(
            ModelContainer container,
            ModelSearchCriteria criteria,
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);

            if (criteria == null) return true;
            if (container?.Metadata == null) return false;

            if (!string.IsNullOrEmpty(criteria.Name) &&
                (container.Metadata.Name?.Contains(criteria.Name, StringComparison.OrdinalIgnoreCase) != true))
                return false;

            if (!string.IsNullOrEmpty(criteria.Framework) &&
                !string.Equals(container.Metadata.Framework, criteria.Framework, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(criteria.Type) &&
                !string.Equals(container.Metadata.ModelType, criteria.Type, StringComparison.OrdinalIgnoreCase))
                return false;

            if (!string.IsNullOrEmpty(criteria.Version) &&
                !string.Equals(container.Metadata.Version, criteria.Version, StringComparison.OrdinalIgnoreCase))
                return false;

            if (criteria.FromDate.HasValue && container.Metadata.CreatedDate < criteria.FromDate.Value)
                return false;

            if (criteria.ToDate.HasValue && container.Metadata.CreatedDate > criteria.ToDate.Value)
                return false;

            if (criteria.MinAccuracy.HasValue)
            {
                if (_performanceMetrics.TryGetValue(container.ModelId, out var metrics))
                {
                    if (metrics.SuccessRate < criteria.MinAccuracy.Value)
                        return false;
                }
            }

            return true;
        }

        private bool MatchesCriteria(ModelSearchItem item, ModelSearchCriteria criteria)
        {
            if (criteria == null) return true;

            if (!string.IsNullOrEmpty(criteria.Name) &&
                item.Metadata?.Name?.Contains(criteria.Name, StringComparison.OrdinalIgnoreCase) != true)
                return false;

            return true;
        }

        private async Task MonitorModelsAsync()
        {
            var now = DateTime.UtcNow;

            foreach (var container in _models.Values)
            {
                try
                {
                    var idle = now - container.LastAccessed;

                    if (container.Status == ModelStatus.Loaded && idle > TimeSpan.FromHours(1))
                    {
                        UnloadModel(container.ModelId);
                    }

                    // bütünlük kontrol (günlük)
                    if (now - container.LastIntegrityCheck > TimeSpan.FromDays(1))
                    {
                        await CheckModelIntegrityAsync(container).ConfigureAwait(false);
                        container.LastIntegrityCheck = now;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Model izleme sırasında hata: {ModelId}", container.ModelId);
                }
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task CheckForUpdatesAsync()
        {
            if (!IsAutoUpdateEnabled) return;

            try
            {
                foreach (var container in _models.Values)
                {
                    if (container.Metadata?.AutoUpdate == true && container.Metadata.UpdateUrl != null)
                    {
                        await CheckModelUpdateAsync(container).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Güncelleme kontrolü sırasında hata");
            }
        }

        private async Task PerformHealthCheckAsync()
        {
            _lastHealthCheck = DateTime.UtcNow;

            var health = new ModelManagerHealth
            {
                Timestamp = _lastHealthCheck,
                Status = HealthStatus.Healthy,
                Issues = new List<string>()
            };

            try
            {
                var root = Path.GetPathRoot(_modelRepositoryPath);
                if (!string.IsNullOrWhiteSpace(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.AvailableFreeSpace < 100 * 1024 * 1024)
                    {
                        health.Status = HealthStatus.Warning;
                        health.Issues.Add($"Düşük disk alanı: {drive.AvailableFreeSpace / (1024 * 1024)}MB");
                    }
                }

                if (!VerifyRepositoryIntegrity())
                {
                    health.Status = HealthStatus.Error;
                    health.Issues.Add("Repository bütünlüğü bozuk");
                }

                var successRate = _performanceMetrics.Values
                    .Where(m => m.TotalInferences > 0)
                    .Select(m => m.SuccessRate)
                    .DefaultIfEmpty(1)
                    .Average();

                if (successRate < 0.8)
                {
                    health.Status = HealthStatus.Warning;
                    health.Issues.Add($"Düşük model başarı oranı: %{successRate * 100:F2}");
                }
            }
            catch (Exception ex)
            {
                health.Status = HealthStatus.Error;
                health.Issues.Add($"Sağlık kontrolü hatası: {ex.Message}");
            }

            OnModelEvent(new ModelEventArgs
            {
                EventType = ModelEventType.HealthCheck,
                ModelId = null,
                Message = $"Sağlık kontrolü: {health.Status}",
                Severity = health.Status == HealthStatus.Healthy ? ModelSeverity.Information :
                          health.Status == HealthStatus.Warning ? ModelSeverity.Warning : ModelSeverity.Error,
                Metadata = null,
                AdditionalData = new Dictionary<string, object> { ["Health"] = health }
            });

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task CleanupExpiredModelsAsync()
        {
            var expired = _models.Values
                .Where(m => m.Metadata?.ExpirationDate.HasValue == true &&
                            m.Metadata.ExpirationDate.Value < DateTime.UtcNow)
                .ToList();

            foreach (var model in expired)
            {
                try
                {
                    await ArchiveModelAsync(model.ModelId, ArchiveReason.Expired).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Süresi dolmuş model arşivlenemedi: {ModelId}", model.ModelId);
                }
            }
        }

        private async Task CleanupOldCacheAsync()
        {
            try
            {
                if (!Directory.Exists(_cachePath))
                    return;

                var files = Directory.GetFiles(_cachePath, "*.cache");
                var cutoff = DateTime.UtcNow.AddDays(-7);

                foreach (var f in files)
                {
                    try
                    {
                        if (File.GetLastAccessTimeUtc(f) < cutoff)
                            File.Delete(f);
                    }
                    catch { /* ignore */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Cache temizleme sırasında hata");
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task CleanupCompletedTrainingsAsync()
        {
            var completed = _trainingSessions.Values
                .Where(s => s.Status == TrainingStatus.Completed ||
                            s.Status == TrainingStatus.Failed ||
                            s.Status == TrainingStatus.Cancelled)
                .Where(s => s.EndTime.HasValue && s.EndTime.Value < DateTime.UtcNow.AddHours(-1))
                .ToList();

            foreach (var s in completed)
            {
                _trainingSessions.TryRemove(s.SessionId, out _);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task ClearArchivedModelsAsync()
        {
            var archivePath = Path.Combine(_modelRepositoryPath, "archive");
            if (!Directory.Exists(archivePath))
                return;

            try
            {
                Directory.Delete(archivePath, true);
                Directory.CreateDirectory(archivePath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Arşivlenmiş modeller temizlenemedi");
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task CheckModelIntegrityAsync(ModelContainer container)
        {
            try
            {
                using var sha256 = System.Security.Cryptography.SHA256.Create();

                var currentHash = sha256.ComputeHash(container.ModelData);
                var storedHashB64 = container.Metadata?.Hash;

                if (!string.IsNullOrWhiteSpace(storedHashB64))
                {
                    var storedHash = Convert.FromBase64String(storedHashB64);
                    if (!currentHash.SequenceEqual(storedHash))
                    {
                        container.Status = ModelStatus.Corrupted;

                        LogModelEvent(
                            ModelEventType.ModelCorrupted,
                            $"Model bütünlüğü bozuk: {container.Metadata?.Name}",
                            container.ModelId,
                            ModelSeverity.Error,
                            metadata: container.Metadata);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Model bütünlük kontrolü sırasında hata: {ModelId}", container.ModelId);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task CheckModelUpdateAsync(ModelContainer container)
        {
            try
            {
                using var client = new System.Net.Http.HttpClient();
                var response = await client.GetAsync($"{container.Metadata.UpdateUrl}/version").ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) return;

                var latestVersion = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (Version.TryParse(latestVersion, out var latest) &&
                    Version.TryParse(container.Metadata.Version, out var current) &&
                    latest > current)
                {
                    LogModelEvent(
                        ModelEventType.UpdateAvailable,
                        $"Model güncellemesi mevcut: {container.Metadata.Version} -> {latestVersion}",
                        container.ModelId,
                        ModelSeverity.Information,
                        metadata: container.Metadata);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Model güncelleme kontrolü sırasında hata: {ModelId}", container.ModelId);
            }
        }

        private void UnloadModelInternal(ModelContainer container)
        {
            if (container.SessionData is IDisposable d)
            {
                try { d.Dispose(); } catch { /* ignore */ }
            }

            container.SessionData = null;
            container.SessionOptions = null;
        }

        // ---- Validation helpers ----
        private void ValidateDisposed()
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(ModelManager));
        }

        private void ValidateModelId(string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                throw new ArgumentException("ModelId boş olamaz", nameof(modelId));
        }

        private void ValidateMetadata(ModelMetadata metadata)
        {
            if (metadata == null)
                throw new ArgumentNullException(nameof(metadata));

            if (string.IsNullOrWhiteSpace(metadata.Name))
                throw new ArgumentException("Model adı boş olamaz", nameof(metadata));

            if (string.IsNullOrWhiteSpace(metadata.Version))
                throw new ArgumentException("Model versiyonu boş olamaz", nameof(metadata));

            if (string.IsNullOrWhiteSpace(metadata.Framework))
                throw new ArgumentException("Model framework'ü boş olamaz", nameof(metadata));
        }

        private void ValidateTrainingRequest(ModelTrainingRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.ModelName))
                throw new ArgumentException("ModelName boş olamaz", nameof(request));

            if (request.Epochs <= 0)
                throw new ArgumentException("Epochs 0'dan büyük olmalı", nameof(request));
        }

        private void HandleInitializationError(Exception ex)
        {
            try
            {
                _logger?.LogCritical(ex, "ModelManager initialization failed");
            }
            catch { /* ignore */ }

            try
            {
                // Recovery Engine varsa tetikle (placeholder)
                // _recoveryEngine?.Handle(ex);
            }
            catch { /* ignore */ }
        }

        // ---- IDisposable ----
        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;

            try { _cancellationTokenSource.Cancel(); } catch { /* ignore */ }

            try { _deploymentTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            try { _monitoringTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            try { _cleanupTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }

            try { _deploymentLock?.Dispose(); } catch { /* ignore */ }
            try { _modelLock?.Dispose(); } catch { /* ignore */ }
            try { _cancellationTokenSource?.Dispose(); } catch { /* ignore */ }

            // Unload all
            foreach (var kv in _models)
            {
                try { UnloadModelInternal(kv.Value); } catch { /* ignore */ }
            }

            _models.Clear();
            _trainingSessions.Clear();
            _performanceMetrics.Clear();

            try
            {
                LogModelEvent(ModelEventType.SystemShutdown, "ModelManager dispose edildi", null, ModelSeverity.Information);
            }
            catch { /* ignore */ }
        }

        #endregion
    }
}
