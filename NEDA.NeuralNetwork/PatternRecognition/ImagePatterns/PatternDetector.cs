using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Configuration;
using NEDA.NeuralNetwork.Common;
using NEDA.NeuralNetwork.DeepLearning;
using NEDA.ComputerVision;
using Accord.Imaging;
using Accord.Imaging.Filters;

namespace NEDA.NeuralNetwork.PatternRecognition.ImagePatterns;
{
    /// <summary>
    /// Görüntü desen tanıma motoru - Görsel desen tespiti, nesne tanıma ve şekil analizi;
    /// Çoklu algoritma ve derin öğrenme yöntemlerini birleştirir;
    /// </summary>
    public class PatternDetector : IPatternDetector, IDisposable;
    {
        private readonly ILogger<PatternDetector> _logger;
        private readonly PatternDetectionConfig _config;
        private readonly IFeatureDetector _featureDetector;
        private readonly IImageClassifier _imageClassifier;
        private readonly IDeepLearningModel _deepLearningModel;
        private readonly ITemplateMatcher _templateMatcher;
        private readonly IImagePreprocessor _preprocessor;
        private readonly ConcurrentPatternCache _patternCache;
        private readonly SemaphoreSlim _processingLock;
        private readonly List<DetectionPattern> _registeredPatterns;
        private readonly Dictionary<string, PatternModel> _patternModels;
        private readonly MetricsCollector _metricsCollector;
        private bool _isInitialized;
        private bool _isDisposed;

        /// <summary>
        /// Desen dedektörünü başlatır;
        /// </summary>
        public PatternDetector(
            ILogger<PatternDetector> logger,
            IOptions<PatternDetectionConfig> config,
            IFeatureDetector featureDetector = null,
            IImageClassifier imageClassifier = null,
            IDeepLearningModel deepLearningModel = null,
            ITemplateMatcher templateMatcher = null,
            IImagePreprocessor preprocessor = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? PatternDetectionConfig.Default;

            _featureDetector = featureDetector ?? new FeatureDetector();
            _imageClassifier = imageClassifier ?? new ImageClassifier();
            _deepLearningModel = deepLearningModel ?? LoadDefaultModel();
            _templateMatcher = templateMatcher ?? new TemplateMatcher();
            _preprocessor = preprocessor ?? new ImagePreprocessor();

            _patternCache = new ConcurrentPatternCache(_config.CacheSize);
            _processingLock = new SemaphoreSlim(1, 1);
            _registeredPatterns = new List<DetectionPattern>();
            _patternModels = new Dictionary<string, PatternModel>();
            _metricsCollector = new MetricsCollector("PatternDetector");

            _logger.LogInformation("PatternDetector initialized. Detection Methods: {MethodCount}",
                _config.DetectionMethods.Count);
        }

        /// <summary>
        /// Asenkron başlatma işlemi;
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            try
            {
                await ValidateConfigurationAsync();
                await LoadPatternDatabaseAsync();
                await InitializeDetectionModelsAsync();
                await WarmupModelsAsync();

                _isInitialized = true;
                _logger.LogInformation("PatternDetector initialization completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize PatternDetector");
                throw new PatternDetectorInitializationException(
                    "PatternDetector initialization failed", ex);
            }
        }

        /// <summary>
        /// Görüntüdeki desenleri tespit eder;
        /// </summary>
        public async Task<PatternDetectionResult> DetectPatternsAsync(
            Bitmap image,
            DetectionOptions options = null)
        {
            ValidateImage(image);
            options ??= DetectionOptions.Default;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            try
            {
                await _processingLock.WaitAsync();

                // Önbellek kontrolü;
                var cacheKey = GenerateCacheKey(image, options);
                if (_patternCache.TryGet(cacheKey, out var cachedResult))
                {
                    _metricsCollector.IncrementCounter("cache_hits");
                    return cachedResult;
                }

                // Ön işleme;
                var processedImage = await PreprocessImageAsync(image, options.Preprocessing);

                // Çoklu algoritma ile tespit;
                var detectionTasks = new List<Task<PatternDetectionResult>>();

                if (options.UseFeatureDetection)
                {
                    detectionTasks.Add(DetectUsingFeaturesAsync(processedImage, options));
                }

                if (options.UseTemplateMatching && _registeredPatterns.Any())
                {
                    detectionTasks.Add(DetectUsingTemplatesAsync(processedImage, options));
                }

                if (options.UseDeepLearning && _deepLearningModel != null)
                {
                    detectionTasks.Add(DetectUsingDeepLearningAsync(processedImage, options));
                }

                if (options.UseContourAnalysis)
                {
                    detectionTasks.Add(DetectUsingContoursAsync(processedImage, options));
                }

                var results = await Task.WhenAll(detectionTasks);
                var combinedResult = CombineDetectionResults(results, options);

                // Sonuç iyileştirme;
                combinedResult = await RefineDetectionResultAsync(combinedResult, processedImage, options);

                // Sonuçları analiz et;
                AnalyzeDetectionResults(combinedResult);

                // Önbelleğe ekle;
                _patternCache.Add(cacheKey, combinedResult);

                stopwatch.Stop();
                combinedResult.ProcessingTime = stopwatch.Elapsed;
                combinedResult.Timestamp = DateTime.UtcNow;

                _logger.LogDebug("Pattern detection completed: {PatternCount} patterns found in {ProcessingTime}ms",
                    combinedResult.DetectedPatterns.Count, combinedResult.ProcessingTime.TotalMilliseconds);

                return combinedResult;
            }
            finally
            {
                _processingLock.Release();
            }
        }

        /// <summary>
        /// Görüntü dizisinde desenleri tespit eder;
        /// </summary>
        public async Task<BatchDetectionResult> DetectPatternsInBatchAsync(
            IEnumerable<Bitmap> images,
            BatchDetectionOptions options = null)
        {
            ValidateImageBatch(images);
            options ??= BatchDetectionOptions.Default;

            var imageList = images.ToList();
            var results = new ConcurrentBag<PatternDetectionResult>();
            var overallResult = new BatchDetectionResult();

            var parallelOptions = new ParallelOptions;
            {
                MaxDegreeOfParallelism = options.MaxParallelism,
                CancellationToken = options.CancellationToken;
            };

            await Parallel.ForEachAsync(imageList, parallelOptions, async (image, cancellationToken) =>
            {
                try
                {
                    var detectionOptions = options.DetectionOptions ?? DetectionOptions.Default;
                    var result = await DetectPatternsAsync(image, detectionOptions);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to detect patterns in image");
                }
            });

            // Sonuçları birleştir;
            overallResult.IndividualResults = results.ToList();
            overallResult.OverallStatistics = CalculateBatchStatistics(overallResult.IndividualResults);
            overallResult.CrossImagePatterns = FindCrossImagePatterns(overallResult.IndividualResults);
            overallResult.Timestamp = DateTime.UtcNow;

            _logger.LogInformation("Batch detection completed: {ImageCount} images, {TotalPatterns} total patterns",
                imageList.Count, overallResult.OverallStatistics.TotalPatterns);

            return overallResult;
        }

        /// <summary>
        /// Video akışında gerçek zamanlı desen tespiti;
        /// </summary>
        public async Task<RealTimeDetectionResult> StartRealTimeDetectionAsync(
            IVideoStream videoStream,
            RealTimeDetectionOptions options)
        {
            ValidateVideoStream(videoStream);
            ValidateRealTimeOptions(options);

            var detector = new RealTimePatternDetector(
                videoStream,
                options,
                this,
                _logger);

            await detector.InitializeAsync();
            await detector.StartDetectionAsync();

            return new RealTimeDetectionResult;
            {
                Detector = detector,
                Status = DetectionStatus.Running,
                StartedAt = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Yeni desen kalıbı kaydeder;
        /// </summary>
        public async Task<DetectionPattern> RegisterPatternAsync(
            string patternName,
            Bitmap templateImage,
            PatternRegistrationOptions options = null)
        {
            ValidateTemplateImage(templateImage);
            options ??= PatternRegistrationOptions.Default;

            var pattern = await CreateDetectionPatternAsync(patternName, templateImage, options);

            lock (_registeredPatterns)
            {
                _registeredPatterns.Add(pattern);
            }

            // Desen modelini eğit;
            await TrainPatternModelAsync(pattern);

            _logger.LogInformation("Pattern registered: {PatternName} (ID: {PatternId})",
                patternName, pattern.PatternId);

            return pattern;
        }

        /// <summary>
        /// Desen eşleştirme yapar;
        /// </summary>
        public async Task<PatternMatchingResult> MatchPatternAsync(
            Bitmap sourceImage,
            DetectionPattern pattern,
            MatchingOptions options = null)
        {
            ValidateSourceImage(sourceImage);
            options ??= MatchingOptions.Default;

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var matches = await _templateMatcher.MatchAsync(sourceImage, pattern.TemplateImage, options);
            var confidence = CalculateMatchingConfidence(matches, pattern);
            var location = CalculateMatchLocation(matches);

            var result = new PatternMatchingResult;
            {
                PatternId = pattern.PatternId,
                PatternName = pattern.Name,
                Matches = matches,
                Confidence = confidence,
                BestMatchLocation = location,
                IsMatch = confidence >= options.ConfidenceThreshold,
                ProcessingTime = stopwatch.Elapsed;
            };

            if (result.IsMatch)
            {
                _logger.LogDebug("Pattern match found: {PatternName} with {Confidence:F2} confidence",
                    pattern.Name, confidence);
            }

            return result;
        }

        /// <summary>
        /// Çoklu desen eşleştirme yapar;
        /// </summary>
        public async Task<MultiPatternMatchResult> MatchMultiplePatternsAsync(
            Bitmap sourceImage,
            IEnumerable<DetectionPattern> patterns,
            MultiMatchingOptions options = null)
        {
            ValidateSourceImage(sourceImage);
            options ??= MultiMatchingOptions.Default;

            var patternList = patterns.ToList();
            var matchTasks = patternList.Select(pattern =>
                MatchPatternAsync(sourceImage, pattern, options.MatchingOptions));

            var results = await Task.WhenAll(matchTasks);

            var validMatches = results;
                .Where(r => r.IsMatch)
                .OrderByDescending(r => r.Confidence)
                .Take(options.MaxMatches)
                .ToList();

            return new MultiPatternMatchResult;
            {
                AllResults = results,
                BestMatches = validMatches,
                TotalMatches = validMatches.Count,
                ProcessingTime = CalculateTotalProcessingTime(results)
            };
        }

        /// <summary>
        /// Desen sınıflandırma yapar;
        /// </summary>
        public async Task<PatternClassificationResult> ClassifyPatternAsync(
            Bitmap image,
            ClassificationOptions options = null)
        {
            ValidateImage(image);
            options ??= ClassificationOptions.Default;

            var features = await ExtractPatternFeaturesAsync(image, options.FeatureExtraction);
            var classification = await _imageClassifier.ClassifyAsync(features, options.Classification);

            var result = new PatternClassificationResult;
            {
                PatternType = classification.PatternType,
                Confidence = classification.Confidence,
                AllProbabilities = classification.Probabilities,
                Features = features,
                ClassificationTime = DateTime.UtcNow;
            };

            // Detaylı analiz;
            if (options.PerformDetailedAnalysis)
            {
                result.DetailedAnalysis = await PerformDetailedAnalysisAsync(image, classification);
            }

            return result;
        }

        /// <summary>
        /// Desen yoğunluk analizi yapar;
        /// </summary>
        public async Task<PatternDensityResult> AnalyzePatternDensityAsync(
            Bitmap image,
            DensityAnalysisOptions options = null)
        {
            ValidateImage(image);
            options ??= DensityAnalysisOptions.Default;

            var densityMap = await CalculateDensityMapAsync(image, options);
            var statistics = CalculateDensityStatistics(densityMap);
            var hotspots = FindDensityHotspots(densityMap, options.HotspotThreshold);

            return new PatternDensityResult;
            {
                DensityMap = densityMap,
                DensityStatistics = statistics,
                Hotspots = hotspots,
                OverallDensity = statistics.MeanDensity,
                UniformityScore = CalculateUniformityScore(densityMap)
            };
        }

        /// <summary>
        /// Desen tekrar analizi yapar;
        /// </summary>
        public async Task<PatternRepetitionResult> AnalyzePatternRepetitionAsync(
            Bitmap image,
            RepetitionAnalysisOptions options = null)
        {
            ValidateImage(image);
            options ??= RepetitionAnalysisOptions.Default;

            var patterns = await DetectPatternsAsync(image, options.DetectionOptions);
            var repetitionAnalysis = AnalyzeRepetition(patterns.DetectedPatterns, options);

            return new PatternRepetitionResult;
            {
                DetectedPatterns = patterns.DetectedPatterns,
                RepetitionPatterns = repetitionAnalysis.RepetitionPatterns,
                Periodicity = repetitionAnalysis.Periodicity,
                RegularityScore = repetitionAnalysis.RegularityScore,
                SymmetryAnalysis = await AnalyzeSymmetryAsync(image, patterns)
            };
        }

        /// <summary>
        /// Desen öğrenme ve model güncellemesi yapar;
        /// </summary>
        public async Task<LearningResult> LearnFromDetectionsAsync(
            IEnumerable<LearningSample> samples,
            LearningOptions options = null)
        {
            ValidateLearningSamples(samples);
            options ??= LearningOptions.Default;

            var sampleList = samples.ToList();
            var learningContext = new LearningContext;
            {
                Samples = sampleList,
                Options = options,
                CurrentPatterns = _registeredPatterns;
            };

            var result = await PerformLearningAsync(learningContext);

            if (result.Success)
            {
                await UpdateDetectionModelsAsync(result);
                await UpdatePatternDatabaseAsync(result.NewPatterns);
            }

            _logger.LogInformation("Learning completed: {NewPatterns} new patterns learned",
                result.NewPatterns.Count);

            return result;
        }

        /// <summary>
        /// Performans optimizasyonu yapar;
        /// </summary>
        public async Task<OptimizationResult> OptimizeDetectionAsync(
            OptimizationOptions options = null)
        {
            options ??= OptimizationOptions.Default;

            var metrics = await CollectPerformanceMetricsAsync();
            var optimizationPlan = CreateOptimizationPlan(metrics, options);

            var result = new OptimizationResult;
            {
                InitialMetrics = metrics,
                OptimizationPlan = optimizationPlan,
                AppliedOptimizations = new List<string>()
            };

            // Optimizasyonları uygula;
            foreach (var optimization in optimizationPlan.Optimizations)
            {
                try
                {
                    await ApplyOptimizationAsync(optimization);
                    result.AppliedOptimizations.Add(optimization.Name);
                    _logger.LogDebug("Applied optimization: {OptimizationName}", optimization.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply optimization: {OptimizationName}", optimization.Name);
                }
            }

            // Optimizasyon sonuçlarını ölç;
            var finalMetrics = await CollectPerformanceMetricsAsync();
            result.FinalMetrics = finalMetrics;
            result.ImprovementPercentage = CalculateImprovementPercentage(metrics, finalMetrics);

            return result;
        }

        #region Private Implementation;

        private async Task<Bitmap> PreprocessImageAsync(Bitmap image, PreprocessingOptions options)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            var processedImage = await _preprocessor.ProcessAsync(image, options);

            stopwatch.Stop();
            _metricsCollector.RecordLatency("preprocessing", stopwatch.Elapsed);

            return processedImage;
        }

        private async Task<PatternDetectionResult> DetectUsingFeaturesAsync(
            Bitmap image,
            DetectionOptions options)
        {
            var features = await _featureDetector.DetectAsync(image, options.FeatureOptions);
            var patterns = ExtractPatternsFromFeatures(features, options);

            return new PatternDetectionResult;
            {
                DetectedPatterns = patterns,
                DetectionMethod = DetectionMethod.FeatureBased,
                Confidence = CalculateFeatureBasedConfidence(features),
                FeaturePoints = features.KeyPoints;
            };
        }

        private async Task<PatternDetectionResult> DetectUsingTemplatesAsync(
            Bitmap image,
            DetectionOptions options)
        {
            var matches = new List<PatternMatch>();

            foreach (var pattern in _registeredPatterns)
            {
                var matchResult = await MatchPatternAsync(image, pattern, new MatchingOptions;
                {
                    ConfidenceThreshold = options.TemplateMatchingThreshold,
                    MaxMatches = 10;
                });

                if (matchResult.IsMatch)
                {
                    matches.Add(new PatternMatch;
                    {
                        Pattern = pattern,
                        Confidence = matchResult.Confidence,
                        Location = matchResult.BestMatchLocation;
                    });
                }
            }

            return new PatternDetectionResult;
            {
                DetectedPatterns = matches,
                DetectionMethod = DetectionMethod.TemplateMatching,
                Confidence = matches.Any() ? matches.Max(m => m.Confidence) : 0;
            };
        }

        private async Task<PatternDetectionResult> DetectUsingDeepLearningAsync(
            Bitmap image,
            DetectionOptions options)
        {
            var input = await PrepareModelInputAsync(image);
            var predictions = await _deepLearningModel.PredictAsync(input, options.ModelOptions);

            var patterns = ConvertPredictionsToPatterns(predictions, options);

            return new PatternDetectionResult;
            {
                DetectedPatterns = patterns,
                DetectionMethod = DetectionMethod.DeepLearning,
                Confidence = predictions.Confidence,
                ModelMetadata = predictions.Metadata;
            };
        }

        private async Task<PatternDetectionResult> DetectUsingContoursAsync(
            Bitmap image,
            DetectionOptions options)
        {
            var contours = await ExtractContoursAsync(image);
            var patterns = AnalyzeContoursForPatterns(contours, options);

            return new PatternDetectionResult;
            {
                DetectedPatterns = patterns,
                DetectionMethod = DetectionMethod.ContourAnalysis,
                Confidence = CalculateContourConfidence(contours),
                Contours = contours;
            };
        }

        private PatternDetectionResult CombineDetectionResults(
            PatternDetectionResult[] results,
            DetectionOptions options)
        {
            if (results.Length == 1)
                return results[0];

            var combined = new PatternDetectionResult;
            {
                DetectionMethod = DetectionMethod.Combined,
                CombinedMethods = results.Select(r => r.DetectionMethod).ToList()
            };

            // Desenleri birleştir ve çakışmaları çöz;
            var allPatterns = results.SelectMany(r => r.DetectedPatterns).ToList();
            combined.DetectedPatterns = MergeOverlappingPatterns(allPatterns, options.MergeThreshold);

            // Güven skorlarını birleştir;
            combined.Confidence = CalculateCombinedConfidence(results);

            // Detaylı sonuçları kaydet;
            combined.ComponentResults = results.ToList();

            return combined;
        }

        private async Task<PatternDetectionResult> RefineDetectionResultAsync(
            PatternDetectionResult result,
            Bitmap image,
            DetectionOptions options)
        {
            if (!options.PerformRefinement)
                return result;

            // Yanlış pozitifleri filtrele;
            var filteredPatterns = await FilterFalsePositivesAsync(result.DetectedPatterns, image, options);
            result.DetectedPatterns = filteredPatterns;

            // Güven skorlarını yeniden hesapla;
            foreach (var pattern in result.DetectedPatterns)
            {
                pattern.Confidence = await RecalculateConfidenceAsync(pattern, image, options);
            }

            // Desenleri sırala;
            result.DetectedPatterns = result.DetectedPatterns;
                .OrderByDescending(p => p.Confidence)
                .ThenByDescending(p => p.Area)
                .ToList();

            return result;
        }

        private string GenerateCacheKey(Bitmap image, DetectionOptions options)
        {
            using var ms = new MemoryStream();
            image.Save(ms, ImageFormat.Png);
            var imageHash = ComputeHash(ms.ToArray());
            return $"{imageHash}_{options.GetHashCode()}_{image.Width}x{image.Height}";
        }

        private byte[] ComputeHash(byte[] data)
        {
            using var sha256 = System.Security.Cryptography.SHA256.Create();
            return sha256.ComputeHash(data);
        }

        private async Task<DetectionPattern> CreateDetectionPatternAsync(
            string name,
            Bitmap templateImage,
            PatternRegistrationOptions options)
        {
            var features = await ExtractPatternFeaturesAsync(templateImage, new FeatureExtractionOptions;
            {
                ExtractDescriptors = true,
                ComputeStatistics = true;
            });

            return new DetectionPattern;
            {
                PatternId = Guid.NewGuid().ToString(),
                Name = name,
                TemplateImage = templateImage,
                Features = features,
                CreatedAt = DateTime.UtcNow,
                Metadata = options.Metadata,
                Statistics = CalculatePatternStatistics(features)
            };
        }

        private async Task TrainPatternModelAsync(DetectionPattern pattern)
        {
            var trainingData = await GenerateTrainingDataAsync(pattern);
            var model = await _deepLearningModel.TrainAsync(trainingData, new TrainingOptions;
            {
                Epochs = _config.TrainingEpochs,
                LearningRate = _config.LearningRate;
            });

            _patternModels[pattern.PatternId] = model;
        }

        #endregion;

        #region Validation Methods;

        private async Task ValidateConfigurationAsync()
        {
            if (_config.MinimumPatternSize < 2)
                throw new InvalidConfigurationException("Minimum pattern size must be at least 2x2");

            if (_config.ConfidenceThreshold < 0.1 || _config.ConfidenceThreshold > 0.99)
                throw new InvalidConfigurationException("Confidence threshold must be between 0.1 and 0.99");

            if (_config.CacheSize < 0)
                throw new InvalidConfigurationException("Cache size cannot be negative");

            await Task.CompletedTask;
        }

        private void ValidateImage(Bitmap image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            if (image.Width < _config.MinimumImageWidth || image.Height < _config.MinimumImageHeight)
                throw new InvalidImageException($"Image too small. Minimum size: {_config.MinimumImageWidth}x{_config.MinimumImageHeight}");

            if (image.PixelFormat != PixelFormat.Format24bppRgb &&
                image.PixelFormat != PixelFormat.Format32bppArgb)
                throw new InvalidImageException("Unsupported pixel format. Supported formats: 24bppRgb, 32bppArgb");
        }

        private void ValidateImageBatch(IEnumerable<Bitmap> images)
        {
            if (images == null)
                throw new ArgumentNullException(nameof(images));

            var imageList = images.ToList();
            if (!imageList.Any())
                throw new ArgumentException("Image batch cannot be empty", nameof(images));

            if (imageList.Count > _config.MaxBatchSize)
                throw new ArgumentException($"Batch size exceeds maximum of {_config.MaxBatchSize}", nameof(images));
        }

        private void ValidateTemplateImage(Bitmap image)
        {
            ValidateImage(image);

            if (image.Width > _config.MaxTemplateWidth || image.Height > _config.MaxTemplateHeight)
                throw new InvalidImageException($"Template image too large. Maximum size: {_config.MaxTemplateWidth}x{_config.MaxTemplateHeight}");

            var aspectRatio = (double)image.Width / image.Height;
            if (aspectRatio < _config.MinAspectRatio || aspectRatio > _config.MaxAspectRatio)
                throw new InvalidImageException($"Template aspect ratio must be between {_config.MinAspectRatio} and {_config.MaxAspectRatio}");
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    _patternCache?.Dispose();
                    _processingLock?.Dispose();

                    foreach (var pattern in _registeredPatterns)
                    {
                        pattern.TemplateImage?.Dispose();
                    }
                }

                _isDisposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Supporting Types;

    public class PatternDetectionConfig;
    {
        public List<DetectionMethod> DetectionMethods { get; set; } = new List<DetectionMethod>
        {
            DetectionMethod.FeatureBased,
            DetectionMethod.DeepLearning;
        };
        public double ConfidenceThreshold { get; set; } = 0.7;
        public int MinimumImageWidth { get; set; } = 32;
        public int MinimumImageHeight { get; set; } = 32;
        public int MinimumPatternSize { get; set; } = 8;
        public int MaxTemplateWidth { get; set; } = 512;
        public int MaxTemplateHeight { get; set; } = 512;
        public double MinAspectRatio { get; set; } = 0.25;
        public double MaxAspectRatio { get; set; } = 4.0;
        public int CacheSize { get; set; } = 1000;
        public int MaxBatchSize { get; set; } = 100;
        public int TrainingEpochs { get; set; } = 50;
        public double LearningRate { get; set; } = 0.001;
        public TimeSpan ModelUpdateInterval { get; set; } = TimeSpan.FromHours(1);

        public static PatternDetectionConfig Default => new PatternDetectionConfig();
    }

    public class PatternDetectionResult;
    {
        public List<DetectedPattern> DetectedPatterns { get; set; } = new List<DetectedPattern>();
        public DetectionMethod DetectionMethod { get; set; }
        public List<DetectionMethod> CombinedMethods { get; set; } = new List<DetectionMethod>();
        public double Confidence { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public DateTime Timestamp { get; set; }
        public List<PatternDetectionResult> ComponentResults { get; set; } = new List<PatternDetectionResult>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public List<FeaturePoint> FeaturePoints { get; set; } = new List<FeaturePoint>();
        public List<Contour> Contours { get; set; } = new List<Contour>();
        public ModelMetadata ModelMetadata { get; set; }
    }

    public class DetectedPattern;
    {
        public Rectangle BoundingBox { get; set; }
        public double Confidence { get; set; }
        public string PatternType { get; set; }
        public double Area { get; set; }
        public Point Center { get; set; }
        public List<FeatureDescriptor> Features { get; set; } = new List<FeatureDescriptor>();
        public Dictionary<string, object> Attributes { get; set; } = new Dictionary<string, object>();
    }

    public class DetectionPattern;
    {
        public string PatternId { get; set; }
        public string Name { get; set; }
        public Bitmap TemplateImage { get; set; }
        public FeatureSet Features { get; set; }
        public DateTime CreatedAt { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public PatternStatistics Statistics { get; set; }
    }

    public class PatternMatch;
    {
        public DetectionPattern Pattern { get; set; }
        public double Confidence { get; set; }
        public Rectangle Location { get; set; }
        public List<Point> MatchPoints { get; set; } = new List<Point>();
    }

    public class BatchDetectionResult;
    {
        public List<PatternDetectionResult> IndividualResults { get; set; } = new List<PatternDetectionResult>();
        public BatchStatistics OverallStatistics { get; set; }
        public List<CrossImagePattern> CrossImagePatterns { get; set; } = new List<CrossImagePattern>();
        public DateTime Timestamp { get; set; }
        public TimeSpan TotalProcessingTime { get; set; }
    }

    public class RealTimeDetectionResult;
    {
        public RealTimePatternDetector Detector { get; set; }
        public DetectionStatus Status { get; set; }
        public DateTime StartedAt { get; set; }
        public int PatternsDetected { get; set; }
        public double AverageProcessingTime { get; set; }
    }

    public class PatternMatchingResult;
    {
        public string PatternId { get; set; }
        public string PatternName { get; set; }
        public List<PatternMatch> Matches { get; set; } = new List<PatternMatch>();
        public double Confidence { get; set; }
        public Rectangle BestMatchLocation { get; set; }
        public bool IsMatch { get; set; }
        public TimeSpan ProcessingTime { get; set; }
    }

    public class PatternClassificationResult;
    {
        public string PatternType { get; set; }
        public double Confidence { get; set; }
        public Dictionary<string, double> AllProbabilities { get; set; } = new Dictionary<string, double>();
        public FeatureSet Features { get; set; }
        public DateTime ClassificationTime { get; set; }
        public DetailedAnalysis DetailedAnalysis { get; set; }
    }

    public enum DetectionMethod;
    {
        FeatureBased,
        TemplateMatching,
        DeepLearning,
        ContourAnalysis,
        Combined;
    }

    public enum DetectionStatus;
    {
        Initializing,
        Running,
        Paused,
        Stopped,
        Error;
    }

    #endregion;
}
