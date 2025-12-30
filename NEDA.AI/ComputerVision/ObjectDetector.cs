using NEDA.AI.MachineLearning;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.ExceptionHandling.RecoveryStrategies;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring.PerformanceCounters;
using NEDA.Core.Security.Encryption;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace NEDA.AI.ComputerVision
{
    /// <summary>
    /// Advanced AI-powered object detection system with real-time processing capabilities
    /// Supports multiple detection models, ensemble methods, and comprehensive analytics
    /// </summary>
    public class ObjectDetector : IDisposable
    {
        #region Private Fields

        private readonly ILogger _logger;
        private readonly RecoveryEngine _recoveryEngine;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly CryptoEngine _cryptoEngine;
        private readonly List<MLModel> _detectionModels;
        private readonly Dictionary<string, ModelMetadata> _modelMetadata;
        private readonly ObjectTrackingEngine _trackingEngine;
        private readonly DetectionCache _detectionCache;
        private readonly ThreatAnalyzer _threatAnalyzer;

        private bool _disposed = false;
        private bool _isInitialized = false;
        private Stopwatch _processingTimer;
        private int _totalProcessedFrames;
        private DateTime _lastMaintenance;

        #endregion

        #region Public Properties

        /// <summary>
        /// Configuration for object detection operations
        /// </summary>
        public ObjectDetectorConfig Config { get; private set; }

        /// <summary>
        /// Current detection statistics and performance metrics
        /// </summary>
        public DetectionStatistics Statistics { get; private set; }

        /// <summary>
        /// Available object classes for detection
        /// </summary>
        public IReadOnlyDictionary<int, ObjectClass> ObjectClasses => _objectClasses;

        /// <summary>
        /// Event raised when objects are detected in a frame
        /// </summary>
        public event EventHandler<ObjectDetectionEventArgs> ObjectsDetected;

        /// <summary>
        /// Event raised when a high-confidence threat is detected
        /// </summary>
        public event EventHandler<ThreatDetectionEventArgs> ThreatDetected;

        /// <summary>
        /// Event raised for performance metrics and system health
        /// </summary>
        public event EventHandler<DetectionPerformanceEventArgs> PerformanceUpdated;

        #endregion

        #region Private Collections

        private readonly Dictionary<int, ObjectClass> _objectClasses;
        private readonly HashSet<string> _activeModels;
        private readonly ConcurrentQueue<DetectionFrame> _recentFrames;
        private readonly object _processingLock = new object();

        #endregion

        #region Constructor and Initialization

        /// <summary>
        /// Initializes a new instance of the ObjectDetector with advanced AI capabilities
        /// </summary>
        public ObjectDetector(ILogger logger = null)
        {
            _logger = logger ?? LogManager.GetLogger("ObjectDetector");
            _recoveryEngine = new RecoveryEngine(_logger);
            _performanceMonitor = new PerformanceMonitor("ObjectDetector");
            _cryptoEngine = new CryptoEngine();

            _detectionModels = new List<MLModel>();
            _modelMetadata = new Dictionary<string, ModelMetadata>();
            _objectClasses = new Dictionary<int, ObjectClass>();
            _activeModels = new HashSet<string>();
            _recentFrames = new ConcurrentQueue<DetectionFrame>();
            _trackingEngine = new ObjectTrackingEngine();
            _detectionCache = new DetectionCache(TimeSpan.FromMinutes(10));
            _threatAnalyzer = new ThreatAnalyzer();

            Config = LoadConfiguration();
            Statistics = new DetectionStatistics();
            _processingTimer = new Stopwatch();

            InitializeRecoveryStrategies();
            LoadObjectClasses();

            _logger.Info("ObjectDetector instance created");
        }

        /// <summary>
        /// Advanced initialization with custom configuration
        /// </summary>
        public ObjectDetector(ObjectDetectorConfig config, ILogger logger = null) : this(logger)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            InitializeAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronous initialization of AI models and subsystems
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized) return;

            try
            {
                _logger.Info("Initializing ObjectDetector subsystems...");

                await Task.Run(() =>
                {
                    LoadDetectionModels();
                    InitializePerformanceMonitoring();
                    WarmUpModels();
                    StartHealthMonitoring();
                }).ConfigureAwait(false);

                _isInitialized = true;
                _lastMaintenance = DateTime.UtcNow;

                _logger.Info("ObjectDetector initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"ObjectDetector initialization failed: {ex.Message}");
                throw new ObjectDetectionException("Initialization failed", ex);
            }
        }

        private ObjectDetectorConfig LoadConfiguration()
        {
            try
            {
                var settings = SettingsManager.LoadSection<ObjectDetectionSettings>("ObjectDetection");
                return new ObjectDetectorConfig
                {
                    ConfidenceThreshold = settings?.ConfidenceThreshold ?? 0.5f,
                    NmsThreshold = settings?.NmsThreshold ?? 0.4f,
                    MaxDetectionsPerFrame = settings?.MaxDetections ?? 100,
                    ModelType = settings?.ModelType ?? ModelType.YOLOv4,
                    UseEnsemble = settings?.UseEnsemble ?? false,
                    EnableTracking = settings?.EnableTracking ?? true,
                    CacheEnabled = settings?.CacheEnabled ?? true,
                    PerformanceMonitoring = settings?.PerformanceMonitoring ?? true,
                    ThreadCount = Environment.ProcessorCount
                };
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load configuration, using defaults: {ex.Message}");
                return ObjectDetectorConfig.Default;
            }
        }

        private void InitializeRecoveryStrategies()
        {
            _recoveryEngine.AddStrategy<OutOfMemoryException>(new RetryStrategy(2, TimeSpan.FromMilliseconds(500)));
            _recoveryEngine.AddStrategy<ModelLoadException>(new FallbackStrategy(LoadFallbackModel));
            _recoveryEngine.AddStrategy<InvalidOperationException>(new ResetStrategy(ResetDetectorState));
        }

        #endregion

        #region Core Detection Methods

        /// <summary>
        /// Performs advanced object detection on an image with AI-powered analysis
        /// </summary>
        public async Task<ObjectDetectionResult> DetectAsync(Bitmap image, DetectionContext context = null)
        {
            ValidateInitialization();
            ValidateImage(image);

            return await _recoveryEngine.ExecuteWithRecoveryAsync(async () =>
            {
                _processingTimer.Restart();
                var frameId = GenerateFrameId();

                Bitmap preprocessedImage = null;
                try
                {
                    // Check cache for similar frames
                    if (Config.CacheEnabled)
                    {
                        var cachedResult = _detectionCache.Get(image);
                        if (cachedResult != null)
                        {
                            Statistics.CacheHits++;
                            return cachedResult;
                        }
                    }

                    preprocessedImage = await PreprocessImageAsync(image).ConfigureAwait(false);
                    var rawDetections = await RunDetectionModelsAsync(preprocessedImage).ConfigureAwait(false);
                    var processedDetections = ProcessDetections(rawDetections, context);
                    var finalResult = ApplyPostProcessing(processedDetections, frameId, context);

                    // Update tracking
                    if (Config.EnableTracking)
                    {
                        finalResult = _trackingEngine.UpdateTracks(finalResult, frameId);
                    }

                    // Cache result
                    if (Config.CacheEnabled)
                    {
                        _detectionCache.Add(image, finalResult);
                    }

                    // Threat analysis
                    var threats = _threatAnalyzer.AnalyzeThreats(finalResult);
                    if (threats.Any())
                    {
                        RaiseThreatDetectedEvent(threats, finalResult, context);
                    }

                    UpdateStatistics(finalResult, _processingTimer.Elapsed);
                    RaiseObjectsDetectedEvent(finalResult, context);
                    RaisePerformanceEvent();

                    return finalResult;
                }
                finally
                {
                    preprocessedImage?.Dispose();
                    _processingTimer.Stop();
                    _totalProcessedFrames++;
                }
            });
        }

        /// <summary>
        /// Real-time object detection for video streams with temporal analysis
        /// </summary>
        public async Task<VideoDetectionResult> DetectVideoFrameAsync(Bitmap frame, int frameIndex, VideoContext videoContext)
        {
            ValidateInitialization();
            ValidateImage(frame);

            var context = new DetectionContext
            {
                SourceType = DetectionSource.Video,
                Timestamp = DateTime.UtcNow,
                FrameIndex = frameIndex,
                VideoContext = videoContext
            };

            var result = await DetectAsync(frame, context).ConfigureAwait(false);

            // Store recent frame for temporal analysis
            var detectionFrame = new DetectionFrame(frameIndex, result, DateTime.UtcNow);
            _recentFrames.Enqueue(detectionFrame);

            if (_recentFrames.Count > Config.MaxFrameHistory)
            {
                _recentFrames.TryDequeue(out _);
            }

            return new VideoDetectionResult(result, frameIndex, GetTemporalAnalysis());
        }

        /// <summary>
        /// Batch processing for multiple images with optimized resource usage
        /// </summary>
        public async Task<BatchDetectionResult> DetectBatchAsync(IEnumerable<Bitmap> images, BatchDetectionOptions options = null)
        {
            ValidateInitialization();
            options ??= BatchDetectionOptions.Default;

            var result = new BatchDetectionResult();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = options.MaxParallelism ?? Config.ThreadCount
            };

            var imagesList = images.ToList();
            result.TotalImages = imagesList.Count;

            await Task.Run(() =>
            {
                Parallel.ForEach(imagesList, parallelOptions, (image, state, index) =>
                {
                    try
                    {
                        var detectionResult = DetectAsync(image).GetAwaiter().GetResult();
                        result.SuccessfulDetections++;
                        result.Results.Add(detectionResult);

                        options.ProgressCallback?.Invoke((int)index, imagesList.Count, detectionResult);
                    }
                    catch (Exception ex)
                    {
                        result.FailedDetections++;
                        result.Errors.Add(new DetectionError((int)index, ex.Message));
                        _logger.Warning($"Batch detection failed for image {index}: {ex.Message}");
                    }
                });
            }).ConfigureAwait(false);

            result.ProcessingTime = _processingTimer.Elapsed;
            return result;
        }

        /// <summary>
        /// Advanced detection with custom model ensemble and confidence fusion
        /// </summary>
        public async Task<ObjectDetectionResult> DetectWithEnsembleAsync(Bitmap image, IEnumerable<string> modelNames, EnsembleStrategy strategy)
        {
            ValidateInitialization();
            ValidateImage(image);

            _processingTimer.Restart();
            var preprocessedImage = await PreprocessImageAsync(image).ConfigureAwait(false);

            try
            {
                var modelTasks = modelNames.Select(modelName =>
                    RunSingleModelAsync(preprocessedImage, modelName)).ToList();

                var modelResults = await Task.WhenAll(modelTasks).ConfigureAwait(false);
                var fusedDetections = FuseDetections(modelResults, strategy);

                return new ObjectDetectionResult
                {
                    Detections = fusedDetections,
                    ProcessingTime = _processingTimer.Elapsed,
                    ModelName = $"Ensemble_{strategy}",
                    OverallConfidence = CalculateEnsembleConfidence(fusedDetections)
                };
            }
            finally
            {
                preprocessedImage?.Dispose();
                _processingTimer.Stop();
            }
        }

        #endregion

        #region Model Management

        /// <summary>
        /// Loads a custom detection model with validation and optimization
        /// </summary>
        public async Task<bool> LoadModelAsync(string modelPath, ModelLoadOptions options = null)
        {
            options ??= ModelLoadOptions.Default;

            try
            {
                _logger.Info($"Loading detection model from: {modelPath}");

                if (!File.Exists(modelPath))
                {
                    throw new FileNotFoundException($"Model file not found: {modelPath}");
                }

                // Verify model integrity
                var modelHash = _cryptoEngine.ComputeFileHash(modelPath);
                if (options.VerifyIntegrity && !VerifyModelIntegrity(modelPath, modelHash))
                {
                    throw new ModelLoadException($"Model integrity check failed for: {modelPath}");
                }

                var model = await MLModel.LoadAsync(modelPath, options).ConfigureAwait(false);
                model.Name = Path.GetFileNameWithoutExtension(modelPath);

                lock (_processingLock)
                {
                    _detectionModels.Add(model);
                    _activeModels.Add(model.Name);
                    _modelMetadata[model.Name] = new ModelMetadata
                    {
                        LoadTime = DateTime.UtcNow,
                        ModelHash = modelHash,
                        PerformanceStats = new ModelPerformance()
                    };
                }

                _logger.Info($"Model loaded successfully: {model.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load model {modelPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Unloads a model and releases associated resources
        /// </summary>
        public void UnloadModel(string modelName)
        {
            lock (_processingLock)
            {
                var model = _detectionModels.FirstOrDefault(m => m.Name == modelName);
                if (model != null)
                {
                    model.Dispose();
                    _detectionModels.Remove(model);
                    _activeModels.Remove(modelName);
                    _modelMetadata.Remove(modelName);

                    _logger.Info($"Model unloaded: {modelName}");
                }
            }
        }

        /// <summary>
        /// Gets performance statistics for all loaded models
        /// </summary>
        public IReadOnlyDictionary<string, ModelPerformance> GetModelPerformanceStats()
        {
            lock (_processingLock)
            {
                return _modelMetadata.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.PerformanceStats.Clone()
                );
            }
        }

        #endregion

        #region Advanced Detection Features

        /// <summary>
        /// Performs object tracking across multiple frames
        /// </summary>
        public async Task<TrackingResult> TrackObjectsAsync(IEnumerable<Bitmap> frames, TrackingOptions options = null)
        {
            options ??= TrackingOptions.Default;
            var framesList = frames.ToList();
            var trackingResult = new TrackingResult();

            for (int i = 0; i < framesList.Count; i++)
            {
                var frameResult = await DetectVideoFrameAsync(framesList[i], i,
                    new VideoContext { FrameRate = options.FrameRate }).ConfigureAwait(false);

                trackingResult.FrameResults.Add(frameResult);

                if (i % options.KeyframeInterval == 0)
                {
                    trackingResult.Keyframes.Add(i, frameResult);
                }
            }

            trackingResult.Tracks = _trackingEngine.GetActiveTracks();
            return trackingResult;
        }

        /// <summary>
        /// Analyzes detection patterns and behavioral trends
        /// </summary>
        public DetectionAnalytics GetDetectionAnalytics(TimeSpan timeWindow)
        {
            var recentFrames = _recentFrames
                .Where(f => DateTime.UtcNow - f.Timestamp <= timeWindow)
                .ToList();

            if (!recentFrames.Any())
            {
                return new DetectionAnalytics
                {
                    TimeWindow = timeWindow,
                    TotalFrames = 0,
                    AverageConfidence = 0,
                    MostFrequentClass = -1
                };
            }

            return new DetectionAnalytics
            {
                TimeWindow = timeWindow,
                TotalFrames = recentFrames.Count,
                AverageConfidence = recentFrames.Average(f => f.Result.OverallConfidence),
                MostFrequentClass = recentFrames
                    .SelectMany(f => f.Result.Detections)
                    .GroupBy(d => d.ClassId)
                    .OrderByDescending(g => g.Count())
                    .FirstOrDefault()?.Key ?? -1,
                DetectionPatterns = AnalyzeDetectionPatterns(recentFrames),
                PerformanceTrends = CalculatePerformanceTrends(recentFrames)
            };
        }

        /// <summary>
        /// Performs real-time threat assessment on detection results
        /// </summary>
        public ThreatAssessment AssessThreatLevel(ObjectDetectionResult result, ThreatAssessmentContext context)
        {
            return _threatAnalyzer.AssessThreatLevel(result, context);
        }

        #endregion

        #region Private Implementation Methods

        private void LoadDetectionModels()
        {
            try
            {
                var modelDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models", "ObjectDetection");

                if (!Directory.Exists(modelDirectory))
                {
                    _logger.Warning($"Model directory not found: {modelDirectory}");
                    return;
                }

                var modelFiles = Directory.GetFiles(modelDirectory, "*.model")
                    .Concat(Directory.GetFiles(modelDirectory, "*.onnx"))
                    .Concat(Directory.GetFiles(modelDirectory, "*.pb"))
                    .ToArray();

                foreach (var modelFile in modelFiles)
                {
                    try
                    {
                        LoadModelAsync(modelFile).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Failed to load model {modelFile}: {ex.Message}");
                    }
                }

                if (!_detectionModels.Any())
                {
                    throw new ModelLoadException("No detection models could be loaded");
                }

                _logger.Info($"Loaded {_detectionModels.Count} detection models");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load detection models: {ex.Message}");
                throw;
            }
        }

        private void LoadObjectClasses()
        {
            try
            {
                // Load from configuration
                var classesConfig = SettingsManager.LoadSection<ObjectClassesConfig>("ObjectClasses");

                if (classesConfig?.Classes != null)
                {
                    foreach (var classConfig in classesConfig.Classes)
                    {
                        _objectClasses[classConfig.Id] = new ObjectClass
                        {
                            Id = classConfig.Id,
                            Name = classConfig.Name,
                            Category = classConfig.Category,
                            ThreatLevel = classConfig.ThreatLevel,
                            Color = Color.FromArgb(classConfig.ColorArgb),
                            MinimumSize = classConfig.MinimumSize,
                            MaximumSize = classConfig.MaximumSize
                        };
                    }
                }
                else
                {
                    LoadDefaultObjectClasses();
                }

                _logger.Info($"Loaded {_objectClasses.Count} object classes");
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to load object classes, using defaults: {ex.Message}");
                LoadDefaultObjectClasses();
            }
        }

        private void LoadDefaultObjectClasses()
        {
            var defaultClasses = new[]
            {
                new ObjectClass { Id = 0, Name = "Person", Category = "Living", ThreatLevel = ThreatLevel.Low, Color = Color.Red },
                new ObjectClass { Id = 1, Name = "Vehicle", Category = "Transport", ThreatLevel = ThreatLevel.Medium, Color = Color.Blue },
                new ObjectClass { Id = 2, Name = "Animal", Category = "Living", ThreatLevel = ThreatLevel.Low, Color = Color.Green },
                new ObjectClass { Id = 3, Name = "Weapon", Category = "Threat", ThreatLevel = ThreatLevel.High, Color = Color.DarkRed },
                new ObjectClass { Id = 4, Name = "Package", Category = "Object", ThreatLevel = ThreatLevel.Medium, Color = Color.Orange }
            };

            foreach (var objClass in defaultClasses)
            {
                _objectClasses[objClass.Id] = objClass;
            }
        }

        private async Task<Bitmap> PreprocessImageAsync(Bitmap image)
        {
            return await Task.Run(() =>
            {
                // Convert to RGB if necessary
                if (image.PixelFormat != PixelFormat.Format24bppRgb &&
                    image.PixelFormat != PixelFormat.Format32bppRgb)
                {
                    var converted = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(converted))
                    {
                        g.DrawImage(image, 0, 0, image.Width, image.Height);
                    }
                    image.Dispose();
                    return converted;
                }

                return image;
            });
        }

        private async Task<List<Detection>> RunDetectionModelsAsync(Bitmap image)
        {
            var detectionTasks = _detectionModels.Select(model =>
                RunSingleModelAsync(image, model.Name)).ToList();

            var allDetections = await Task.WhenAll(detectionTasks).ConfigureAwait(false);
            return allDetections.SelectMany(d => d).ToList();
        }

        private async Task<List<Detection>> RunSingleModelAsync(Bitmap image, string modelName)
        {
            var model = _detectionModels.FirstOrDefault(m => m.Name == modelName);
            if (model == null)
            {
                throw new ModelNotFoundException($"Model not found: {modelName}");
            }

            var inputTensor = ConvertImageToTensor(image);
            var outputTensor = await model.PredictAsync(inputTensor).ConfigureAwait(false);
            var rawDetections = ParseModelOutput(outputTensor, modelName);

            // Update model performance statistics
            lock (_processingLock)
            {
                if (_modelMetadata.ContainsKey(modelName))
                {
                    var stats = _modelMetadata[modelName].PerformanceStats;
                    stats.TotalInferences++;
                    stats.TotalInferenceTime += _processingTimer.ElapsedMilliseconds;
                    stats.AverageInferenceTime = stats.TotalInferenceTime / stats.TotalInferences;
                }
            }

            return rawDetections;
        }

        private List<Detection> ProcessDetections(List<Detection> rawDetections, DetectionContext context)
        {
            // Apply confidence threshold
            var filteredDetections = rawDetections
                .Where(d => d.Confidence >= Config.ConfidenceThreshold)
                .ToList();

            // Apply Non-Maximum Suppression
            if (Config.NmsThreshold > 0)
            {
                filteredDetections = ApplyNMS(filteredDetections, Config.NmsThreshold);
            }

            // Limit detections per frame
            if (filteredDetections.Count > Config.MaxDetectionsPerFrame)
            {
                filteredDetections = filteredDetections
                    .OrderByDescending(d => d.Confidence)
                    .Take(Config.MaxDetectionsPerFrame)
                    .ToList();
            }

            // Contextual filtering
            if (context?.Filter != null)
            {
                filteredDetections = context.Filter.Apply(filteredDetections);
            }

            return filteredDetections;
        }

        private List<Detection> ApplyNMS(List<Detection> detections, float threshold)
        {
            var result = new List<Detection>();
            var orderedDetections = detections.OrderByDescending(d => d.Confidence).ToList();

            while (orderedDetections.Any())
            {
                var current = orderedDetections[0];
                result.Add(current);
                orderedDetections.RemoveAt(0);

                orderedDetections = orderedDetections.Where(d =>
                    CalculateIoU(current.BoundingBox, d.BoundingBox) < threshold).ToList();
            }

            return result;
        }

        private float CalculateIoU(Rectangle a, Rectangle b)
        {
            var intersection = Rectangle.Intersect(a, b);
            if (intersection.IsEmpty)
                return 0;

            var intersectionArea = intersection.Width * intersection.Height;
            var unionArea = (a.Width * a.Height) + (b.Width * b.Height) - intersectionArea;

            return unionArea > 0 ? intersectionArea / (float)unionArea : 0;
        }

        private ObjectDetectionResult ApplyPostProcessing(List<Detection> detections, string frameId, DetectionContext context)
        {
            return new ObjectDetectionResult
            {
                FrameId = frameId,
                Detections = detections,
                Timestamp = DateTime.UtcNow,
                ProcessingTime = _processingTimer.Elapsed,
                OverallConfidence = detections.Any() ? detections.Average(d => d.Confidence) : 0,
                Context = context,
                ModelName = Config.UseEnsemble ? "Ensemble" : _detectionModels.FirstOrDefault()?.Name
            };
        }

        private List<Detection> FuseDetections(IEnumerable<List<Detection>> modelResults, EnsembleStrategy strategy)
        {
            var allDetections = modelResults.SelectMany(r => r).ToList();

            return strategy switch
            {
                EnsembleStrategy.WeightedAverage => FuseWeightedAverage(allDetections),
                EnsembleStrategy.NonMaximumSuppression => FuseNMS(allDetections),
                EnsembleStrategy.Bayesian => FuseBayesian(allDetections),
                _ => allDetections
            };
        }

        private List<Detection> FuseWeightedAverage(List<Detection> detections)
        {
            var fusedDetections = new List<Detection>();
            var groupedDetections = detections.GroupBy(d => d.ClassId);

            foreach (var group in groupedDetections)
            {
                var weightedConfidence = group.Average(d => d.Confidence);
                var weightedBox = CalculateWeightedBoundingBox(group.ToList());

                fusedDetections.Add(new Detection
                {
                    ClassId = group.Key,
                    Confidence = weightedConfidence,
                    BoundingBox = weightedBox,
                    SourceModel = "Ensemble_Weighted"
                });
            }

            return fusedDetections;
        }

        private Rectangle CalculateWeightedBoundingBox(List<Detection> detections)
        {
            var totalWeight = detections.Sum(d => d.Confidence);
            if (totalWeight == 0) return Rectangle.Empty;

            var x = (int)(detections.Sum(d => d.BoundingBox.X * d.Confidence) / totalWeight);
            var y = (int)(detections.Sum(d => d.BoundingBox.Y * d.Confidence) / totalWeight);
            var width = (int)(detections.Sum(d => d.BoundingBox.Width * d.Confidence) / totalWeight);
            var height = (int)(detections.Sum(d => d.BoundingBox.Height * d.Confidence) / totalWeight);

            return new Rectangle(x, y, width, height);
        }

        #endregion

        #region Utility Methods

        private void ValidateInitialization()
        {
            if (!_isInitialized)
            {
                throw new ObjectDetectionException("ObjectDetector not initialized. Call InitializeAsync() first.");
            }

            if (!_detectionModels.Any())
            {
                throw new ObjectDetectionException("No detection models available");
            }
        }

        private void ValidateImage(Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            if (image.Width < 10 || image.Height < 10)
                throw new ArgumentException("Image dimensions too small for detection");

            if (image.Width > 10000 || image.Height > 10000)
                throw new ArgumentException("Image dimensions too large for detection");
        }

        private string GenerateFrameId()
        {
            return $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}";
        }

        private void UpdateStatistics(ObjectDetectionResult result, TimeSpan processingTime)
        {
            Statistics.TotalFramesProcessed++;
            Statistics.TotalDetections += result.Detections.Count;
            Statistics.AverageProcessingTime = (Statistics.AverageProcessingTime * (Statistics.TotalFramesProcessed - 1) + processingTime.TotalMilliseconds) / Statistics.TotalFramesProcessed;
            Statistics.AverageConfidence = (Statistics.AverageConfidence * (Statistics.TotalFramesProcessed - 1) + result.OverallConfidence) / Statistics.TotalFramesProcessed;

            // Update class-specific statistics
            foreach (var detection in result.Detections)
            {
                if (!Statistics.ClassCounts.ContainsKey(detection.ClassId))
                {
                    Statistics.ClassCounts[detection.ClassId] = 0;
                }
                Statistics.ClassCounts[detection.ClassId]++;
            }

            // Perform maintenance if needed
            if (DateTime.UtcNow - _lastMaintenance > TimeSpan.FromMinutes(5))
            {
                PerformMaintenance();
            }
        }

        private void PerformMaintenance()
        {
            _detectionCache.Cleanup();
            _trackingEngine.CleanupExpiredTracks();
            _lastMaintenance = DateTime.UtcNow;

            _logger.Debug("Performed routine maintenance");
        }

        private void WarmUpModels()
        {
            _logger.Info("Warming up detection models...");

            using (var testImage = new Bitmap(640, 480, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(testImage))
            {
                g.Clear(Color.White);

                foreach (var model in _detectionModels)
                {
                    try
                    {
                        var inputTensor = ConvertImageToTensor(testImage);
                        model.Predict(inputTensor); // Warm-up inference
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning($"Model warm-up failed for {model.Name}: {ex.Message}");
                    }
                }
            }

            _logger.Info("Model warm-up completed");
        }

        private void InitializePerformanceMonitoring()
        {
            _performanceMonitor.AddCounter("FramesProcessed", "Total frames processed");
            _performanceMonitor.AddCounter("Detections", "Total objects detected");
            _performanceMonitor.AddCounter("InferenceTime", "Average inference time");
            _performanceMonitor.AddCounter("CacheHitRate", "Detection cache hit rate");
        }

        private void StartHealthMonitoring()
        {
            Task.Run(async () =>
            {
                while (!_disposed)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                        CheckSystemHealth();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"Health monitoring error: {ex.Message}");
                    }
                }
            });
        }

        private void CheckSystemHealth()
        {
            var memoryUsage = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
            if (memoryUsage > Config.MemoryWarningThresholdMB)
            {
                _logger.Warning($"High memory usage: {memoryUsage}MB");
            }

            if (_detectionModels.Any(m => !m.IsHealthy))
            {
                _logger.Error("One or more detection models are unhealthy");
            }
        }

        #endregion

        #region Event Handlers

        private void RaiseObjectsDetectedEvent(ObjectDetectionResult result, DetectionContext context)
        {
            ObjectsDetected?.Invoke(this, new ObjectDetectionEventArgs
            {
                Result = result,
                Context = context,
                Timestamp = DateTime.UtcNow
            });
        }

        private void RaiseThreatDetectedEvent(List<ThreatDetection> threats, ObjectDetectionResult result, DetectionContext context)
        {
            ThreatDetected?.Invoke(this, new ThreatDetectionEventArgs
            {
                Threats = threats,
                DetectionResult = result,
                Context = context,
                Timestamp = DateTime.UtcNow
            });
        }

        private void RaisePerformanceEvent()
        {
            PerformanceUpdated?.Invoke(this, new DetectionPerformanceEventArgs
            {
                Statistics = Statistics,
                Timestamp = DateTime.UtcNow,
                MemoryUsageMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024,
                ModelPerformance = GetModelPerformanceStats()
            });
        }

        #endregion

        #region Recovery and Fallback Methods

        private MLModel LoadFallbackModel()
        {
            _logger.Warning("Loading fallback detection model");

            // Load a lightweight fallback model
            var fallbackModelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "Models", "Fallback", "fast_detection.model");

            if (File.Exists(fallbackModelPath))
            {
                return MLModel.LoadAsync(fallbackModelPath).GetAwaiter().GetResult();
            }

            throw new ModelLoadException("No fallback model available");
        }

        private void ResetDetectorState()
        {
            _logger.Info("Resetting detector state");

            lock (_processingLock)
            {
                _detectionCache.Clear();
                _trackingEngine.Reset();
                while (_recentFrames.TryDequeue(out _)) { }
            }
        }

        private bool VerifyModelIntegrity(string modelPath, string modelHash)
        {
            try
            {
                // Implement model integrity verification
                var trustedHashes = SettingsManager.LoadSection<ModelIntegrityConfig>("ModelIntegrity");
                return trustedHashes?.TrustedHashes?.ContainsValue(modelHash) ?? false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Tensor Conversion Methods

        private unsafe float[,,,] ConvertImageToTensor(Bitmap image)
        {
            var tensor = new float[1, image.Height, image.Width, 3]; // Batch, Height, Width, Channels

            var bitmapData = image.LockBits(new Rectangle(0, 0, image.Width, image.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);

            try
            {
                var ptr = (byte*)bitmapData.Scan0;

                for (int y = 0; y < image.Height; y++)
                {
                    for (int x = 0; x < image.Width; x++)
                    {
                        int index = y * bitmapData.Stride + x * 3;

                        tensor[0, y, x, 0] = ptr[index + 2] / 255.0f; // R
                        tensor[0, y, x, 1] = ptr[index + 1] / 255.0f; // G
                        tensor[0, y, x, 2] = ptr[index] / 255.0f;     // B
                    }
                }
            }
            finally
            {
                image.UnlockBits(bitmapData);
            }

            return tensor;
        }

        private List<Detection> ParseModelOutput(float[,,] output, string modelName)
        {
            var detections = new List<Detection>();
            int numDetections = output.GetLength(0);

            for (int i = 0; i < numDetections; i++)
            {
                var confidence = output[i, 4, 0];
                if (confidence < Config.ConfidenceThreshold) continue;

                var detection = new Detection
                {
                    ClassId = (int)output[i, 5, 0],
                    Confidence = confidence,
                    BoundingBox = new Rectangle(
                        (int)output[i, 0, 0],
                        (int)output[i, 1, 0],
                        (int)(output[i, 2, 0] - output[i, 0, 0]),
                        (int)(output[i, 3, 0] - output[i, 1, 0])
                    ),
                    SourceModel = modelName
                };

                // Validate detection
                if (IsValidDetection(detection))
                {
                    detections.Add(detection);
                }
            }

            return detections;
        }

        private bool IsValidDetection(Detection detection)
        {
            if (detection.BoundingBox.Width <= 0 || detection.BoundingBox.Height <= 0)
                return false;

            if (detection.Confidence < 0 || detection.Confidence > 1)
                return false;

            if (!_objectClasses.ContainsKey(detection.ClassId))
                return false;

            return true;
        }

        #endregion

        #region Helper Methods (Stub Implementations)

        private List<DetectionPattern> AnalyzeDetectionPatterns(List<DetectionFrame> frames)
        {
            // Implementation for pattern analysis
            return new List<DetectionPattern>();
        }

        private PerformanceTrends CalculatePerformanceTrends(List<DetectionFrame> frames)
        {
            // Implementation for performance trend calculation
            return new PerformanceTrends();
        }

        private TemporalAnalysis GetTemporalAnalysis()
        {
            // Implementation for temporal analysis
            return new TemporalAnalysis();
        }

        private List<Detection> FuseNMS(List<Detection> detections)
        {
            // Implementation for NMS fusion
            return ApplyNMS(detections, Config.NmsThreshold);
        }

        private List<Detection> FuseBayesian(List<Detection> detections)
        {
            // Implementation for Bayesian fusion
            return detections;
        }

        private float CalculateEnsembleConfidence(List<Detection> detections)
        {
            return detections.Any() ? detections.Average(d => d.Confidence) : 0;
        }

        #endregion

        #region IDisposable Implementation

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
                    lock (_processingLock)
                    {
                        foreach (var model in _detectionModels)
                        {
                            model?.Dispose();
                        }
                        _detectionModels.Clear();
                        _activeModels.Clear();
                    }

                    _trackingEngine?.Dispose();
                    _detectionCache?.Dispose();
                    _performanceMonitor?.Dispose();
                    _recoveryEngine?.Dispose();
                    _processingTimer?.Stop();
                }

                _disposed = true;
                _logger.Info("ObjectDetector disposed");
            }
        }

        ~ObjectDetector()
        {
            Dispose(false);
        }

        #endregion
    }

    #region Supporting Classes and Enums

    /// <summary>
    /// Configuration for object detector
    /// </summary>
    public class ObjectDetectorConfig
    {
        public float ConfidenceThreshold { get; set; } = 0.5f;
        public float NmsThreshold { get; set; } = 0.4f;
        public int MaxDetectionsPerFrame { get; set; } = 100;
        public ModelType ModelType { get; set; } = ModelType.YOLOv4;
        public bool UseEnsemble { get; set; } = false;
        public bool EnableTracking { get; set; } = true;
        public bool CacheEnabled { get; set; } = true;
        public bool PerformanceMonitoring { get; set; } = true;
        public int ThreadCount { get; set; } = 4;
        public int MaxFrameHistory { get; set; } = 1000;
        public long MemoryWarningThresholdMB { get; set; } = 1024;

        public static ObjectDetectorConfig Default => new ObjectDetectorConfig();
    }

    /// <summary>
    /// Result of object detection operation
    /// </summary>
    public class ObjectDetectionResult
    {
        public string FrameId { get; set; }
        public List<Detection> Detections { get; set; } = new List<Detection>();
        public DateTime Timestamp { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public float OverallConfidence { get; set; }
        public string ModelName { get; set; }
        public DetectionContext Context { get; set; }
    }

    /// <summary>
    /// Single object detection
    /// </summary>
    public class Detection
    {
        public int ClassId { get; set; }
        public string ClassName => $"Class_{ClassId}";
        public float Confidence { get; set; }
        public Rectangle BoundingBox { get; set; }
        public string SourceModel { get; set; }
        public object Metadata { get; set; }
    }

    /// <summary>
    /// Detection statistics
    /// </summary>
    public class DetectionStatistics
    {
        public long TotalFramesProcessed { get; set; }
        public long TotalDetections { get; set; }
        public double AverageProcessingTime { get; set; }
        public double AverageConfidence { get; set; }
        public long CacheHits { get; set; }
        public Dictionary<int, long> ClassCounts { get; set; } = new Dictionary<int, long>();
    }

    /// <summary>
    /// Object class definition
    /// </summary>
    public class ObjectClass
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public ThreatLevel ThreatLevel { get; set; }
        public Color Color { get; set; }
        public Size MinimumSize { get; set; }
        public Size MaximumSize { get; set; }
    }

    /// <summary>
    /// Detection frame for temporal analysis
    /// </summary>
    public class DetectionFrame
    {
        public int FrameIndex { get; }
        public ObjectDetectionResult Result { get; }
        public DateTime Timestamp { get; }

        public DetectionFrame(int frameIndex, ObjectDetectionResult result, DateTime timestamp)
        {
            FrameIndex = frameIndex;
            Result = result;
            Timestamp = timestamp;
        }
    }

    /// <summary>
    /// Model metadata
    /// </summary>
    public class ModelMetadata
    {
        public DateTime LoadTime { get; set; }
        public string ModelHash { get; set; }
        public ModelPerformance PerformanceStats { get; set; }
    }

    /// <summary>
    /// Model performance statistics
    /// </summary>
    public class ModelPerformance
    {
        public long TotalInferences { get; set; }
        public long TotalInferenceTime { get; set; }
        public double AverageInferenceTime { get; set; }
        public long SuccessfulInferences { get; set; }
        public long FailedInferences { get; set; }

        public ModelPerformance Clone()
        {
            return new ModelPerformance
            {
                TotalInferences = this.TotalInferences,
                TotalInferenceTime = this.TotalInferenceTime,
                AverageInferenceTime = this.AverageInferenceTime,
                SuccessfulInferences = this.SuccessfulInferences,
                FailedInferences = this.FailedInferences
            };
        }
    }

    #endregion

    #region Additional Supporting Classes (Stubs for Compilation)

    // Note: These classes should be implemented in their respective files
    // These are simplified versions for compilation

    public class RecoveryEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Dictionary<Type, IRecoveryStrategy> _strategies = new Dictionary<Type, IRecoveryStrategy>();

        public RecoveryEngine(ILogger logger) => _logger = logger;

        public void AddStrategy<TException>(IRecoveryStrategy strategy) where TException : Exception
        {
            _strategies[typeof(TException)] = strategy;
        }

        public async Task<T> ExecuteWithRecoveryAsync<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_strategies.TryGetValue(ex.GetType(), out var strategy))
                {
                    return await strategy.ExecuteAsync(operation).ConfigureAwait(false);
                }
                throw;
            }
        }

        public void Dispose() { }
    }

    public interface IRecoveryStrategy
    {
        Task<T> ExecuteAsync<T>(Func<Task<T>> operation);
    }

    public class RetryStrategy : IRecoveryStrategy
    {
        private readonly int _maxRetries;
        private readonly TimeSpan _delay;

        public RetryStrategy(int maxRetries, TimeSpan delay)
        {
            _maxRetries = maxRetries;
            _delay = delay;
        }

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
        {
            for (int i = 0; i < _maxRetries; i++)
            {
                try
                {
                    return await operation().ConfigureAwait(false);
                }
                catch
                {
                    if (i == _maxRetries - 1) throw;
                    await Task.Delay(_delay).ConfigureAwait(false);
                }
            }
            throw new InvalidOperationException("Retry strategy failed");
        }
    }

    public class FallbackStrategy : IRecoveryStrategy
    {
        private readonly Func<object> _fallback;

        public FallbackStrategy(Func<object> fallback) => _fallback = fallback;

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch
            {
                return (T)_fallback();
            }
        }
    }

    public class ResetStrategy : IRecoveryStrategy
    {
        private readonly Action _resetAction;

        public ResetStrategy(Action resetAction) => _resetAction = resetAction;

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation)
        {
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch
            {
                _resetAction();
                return await operation().ConfigureAwait(false);
            }
        }
    }

    public class ObjectTrackingEngine : IDisposable
    {
        public ObjectDetectionResult UpdateTracks(ObjectDetectionResult result, string frameId)
        {
            // Tracking implementation
            return result;
        }

        public List<ObjectTrack> GetActiveTracks() => new List<ObjectTrack>();

        public void CleanupExpiredTracks() { }

        public void Reset() { }

        public void Dispose() { }
    }

    public class ObjectTrack { }

    public class DetectionCache : IDisposable
    {
        private readonly TimeSpan _cacheDuration;
        private readonly ConcurrentDictionary<string, (ObjectDetectionResult Result, DateTime Timestamp)> _cache = new();

        public DetectionCache(TimeSpan cacheDuration) => _cacheDuration = cacheDuration;

        public ObjectDetectionResult Get(Bitmap image)
        {
            var hash = ComputeImageHash(image);
            if (_cache.TryGetValue(hash, out var cached) && DateTime.UtcNow - cached.Timestamp < _cacheDuration)
            {
                return cached.Result;
            }
            return null;
        }

        public void Add(Bitmap image, ObjectDetectionResult result)
        {
            var hash = ComputeImageHash(image);
            _cache[hash] = (result, DateTime.UtcNow);
        }

        public void Clear() => _cache.Clear();

        public void Cleanup()
        {
            var cutoff = DateTime.UtcNow - _cacheDuration;
            var expired = _cache.Where(kv => kv.Value.Timestamp < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _cache.TryRemove(key, out _);
            }
        }

        private string ComputeImageHash(Bitmap image)
        {
            using var ms = new MemoryStream();
            image.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }

        public void Dispose() => Clear();
    }

    public class ThreatAnalyzer
    {
        public List<ThreatDetection> AnalyzeThreats(ObjectDetectionResult result)
        {
            // Threat analysis implementation
            return new List<ThreatDetection>();
        }

        public ThreatAssessment AssessThreatLevel(ObjectDetectionResult result, ThreatAssessmentContext context)
        {
            return new ThreatAssessment();
        }
    }

    public class ThreatDetection { }

    public class ThreatAssessment { }

    public class ThreatAssessmentContext { }

    public class DetectionContext
    {
        public DetectionSource SourceType { get; set; }
        public DateTime Timestamp { get; set; }
        public int FrameIndex { get; set; }
        public VideoContext VideoContext { get; set; }
        public IDetectionFilter Filter { get; set; }
    }

    public interface IDetectionFilter
    {
        List<Detection> Apply(List<Detection> detections);
    }

    public class VideoContext
    {
        public double FrameRate { get; set; }
    }

    public class ObjectDetectionEventArgs : EventArgs
    {
        public ObjectDetectionResult Result { get; set; }
        public DetectionContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ThreatDetectionEventArgs : EventArgs
    {
        public List<ThreatDetection> Threats { get; set; }
        public ObjectDetectionResult DetectionResult { get; set; }
        public DetectionContext Context { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class DetectionPerformanceEventArgs : EventArgs
    {
        public DetectionStatistics Statistics { get; set; }
        public DateTime Timestamp { get; set; }
        public long MemoryUsageMB { get; set; }
        public IReadOnlyDictionary<string, ModelPerformance> ModelPerformance { get; set; }
    }

    public class VideoDetectionResult
    {
        public ObjectDetectionResult DetectionResult { get; }
        public int FrameIndex { get; }
        public TemporalAnalysis TemporalAnalysis { get; }

        public VideoDetectionResult(ObjectDetectionResult detectionResult, int frameIndex, TemporalAnalysis temporalAnalysis)
        {
            DetectionResult = detectionResult;
            FrameIndex = frameIndex;
            TemporalAnalysis = temporalAnalysis;
        }
    }

    public class TemporalAnalysis { }

    public class BatchDetectionResult
    {
        public int TotalImages { get; set; }
        public int SuccessfulDetections { get; set; }
        public int FailedDetections { get; set; }
        public List<ObjectDetectionResult> Results { get; set; } = new List<ObjectDetectionResult>();
        public List<DetectionError> Errors { get; set; } = new List<DetectionError>();
        public TimeSpan ProcessingTime { get; set; }
    }

    public class DetectionError
    {
        public int ImageIndex { get; }
        public string ErrorMessage { get; }

        public DetectionError(int imageIndex, string errorMessage)
        {
            ImageIndex = imageIndex;
            ErrorMessage = errorMessage;
        }
    }

    public class BatchDetectionOptions
    {
        public int? MaxParallelism { get; set; }
        public Action<int, int, ObjectDetectionResult> ProgressCallback { get; set; }

        public static BatchDetectionOptions Default => new BatchDetectionOptions();
    }

    public class TrackingResult
    {
        public List<VideoDetectionResult> FrameResults { get; set; } = new List<VideoDetectionResult>();
        public Dictionary<int, VideoDetectionResult> Keyframes { get; set; } = new Dictionary<int, VideoDetectionResult>();
        public List<ObjectTrack> Tracks { get; set; } = new List<ObjectTrack>();
    }

    public class TrackingOptions
    {
        public double FrameRate { get; set; } = 30;
        public int KeyframeInterval { get; set; } = 10;

        public static TrackingOptions Default => new TrackingOptions();
    }

    public class DetectionAnalytics
    {
        public TimeSpan TimeWindow { get; set; }
        public int TotalFrames { get; set; }
        public double AverageConfidence { get; set; }
        public int MostFrequentClass { get; set; }
        public List<DetectionPattern> DetectionPatterns { get; set; }
        public PerformanceTrends PerformanceTrends { get; set; }
    }

    public class DetectionPattern { }

    public class PerformanceTrends { }

    public class ModelLoadOptions
    {
        public bool VerifyIntegrity { get; set; } = true;
        public bool LoadWeights { get; set; } = true;

        public static ModelLoadOptions Default => new ModelLoadOptions();
    }

    public class ModelNotFoundException : Exception
    {
        public ModelNotFoundException(string message) : base(message) { }
    }

    public class ModelLoadException : Exception
    {
        public ModelLoadException(string message) : base(message) { }
        public ModelLoadException(string message, Exception inner) : base(message, inner) { }
    }

    public class ObjectDetectionException : Exception
    {
        public ObjectDetectionException(string message) : base(message) { }
        public ObjectDetectionException(string message, Exception inner) : base(message, inner) { }
    }

    public enum ModelType
    {
        YOLOv4,
        YOLOv5,
        FasterRCNN,
        SSD,
        EfficientDet
    }

    public enum ThreatLevel
    {
        Low,
        Medium,
        High,
        Critical
    }

    public enum DetectionSource
    {
        Image,
        Video,
        Stream,
        Batch
    }

    public enum EnsembleStrategy
    {
        WeightedAverage,
        NonMaximumSuppression,
        Bayesian
    }

    public class ObjectDetectionSettings
    {
        public float ConfidenceThreshold { get; set; }
        public float NmsThreshold { get; set; }
        public int MaxDetections { get; set; }
        public ModelType ModelType { get; set; }
        public bool UseEnsemble { get; set; }
        public bool EnableTracking { get; set; }
        public bool CacheEnabled { get; set; }
        public bool PerformanceMonitoring { get; set; }
    }

    public class ObjectClassesConfig
    {
        public List<ObjectClassConfig> Classes { get; set; }
    }

    public class ObjectClassConfig
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public ThreatLevel ThreatLevel { get; set; }
        public int ColorArgb { get; set; }
        public Size MinimumSize { get; set; }
        public Size MaximumSize { get; set; }
    }

    public class ModelIntegrityConfig
    {
        public Dictionary<string, string> TrustedHashes { get; set; }
    }

    #endregion
}