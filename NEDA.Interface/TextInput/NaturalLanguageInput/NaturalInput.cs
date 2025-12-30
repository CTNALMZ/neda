using NEDA.Brain.IntentRecognition;
using NEDA.Brain.IntentRecognition.ParameterDetection;
using NEDA.Brain.NLP_Engine;
using NEDA.Core.Engine;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Interface.TextInput.NaturalLanguageInput.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.TextInput.NaturalLanguageInput;
{
    /// <summary>
    /// Doğal dil girdilerini işleyen ve yöneten ana sınıf;
    /// </summary>
    public class NaturalInput : INaturalInput;
    {
        private readonly INLPEngine _nlpEngine;
        private readonly IIntentRecognizer _intentRecognizer;
        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly SemaphoreSlim _processingSemaphore;
        private readonly Dictionary<string, UserInputContext> _userContexts;
        private readonly NaturalInputConfiguration _configuration;

        /// <summary>
        /// NaturalInput sınıfı için constructor;
        /// </summary>
        public NaturalInput(
            INLPEngine nlpEngine,
            IIntentRecognizer intentRecognizer,
            ILogger logger,
            IErrorHandler errorHandler,
            NaturalInputConfiguration configuration = null)
        {
            _nlpEngine = nlpEngine ?? throw new ArgumentNullException(nameof(nlpEngine));
            _intentRecognizer = intentRecognizer ?? throw new ArgumentNullException(nameof(intentRecognizer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _configuration = configuration ?? NaturalInputConfiguration.Default;

            _processingSemaphore = new SemaphoreSlim(_configuration.MaxConcurrentProcesses);
            _userContexts = new Dictionary<string, UserInputContext>();
        }

        /// <summary>
        /// Doğal dil girdisini işler ve yapılandırılmış komuta dönüştürür;
        /// </summary>
        public async Task<ProcessedInputResult> ProcessInputAsync(
            string rawInput,
            string userId = null,
            InputContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
                throw new ArgumentException("Input cannot be null or empty", nameof(rawInput));

            await _processingSemaphore.WaitAsync(cancellationToken);

            try
            {
                _logger.LogInformation($"Processing natural language input: {rawInput.Substring(0, Math.Min(rawInput.Length, 100))}...");

                // 1. Ön işleme ve normalizasyon;
                var preprocessedInput = await PreprocessInputAsync(rawInput, cancellationToken);

                // 2. Dil tespiti ve tokenizasyon;
                var languageInfo = await DetectLanguageAsync(preprocessedInput, cancellationToken);
                var tokens = await TokenizeInputAsync(preprocessedInput, languageInfo, cancellationToken);

                // 3. NLP analizi;
                var nlpAnalysis = await PerformNLPAnalysisAsync(tokens, languageInfo, cancellationToken);

                // 4. Niyet tanıma;
                var intentResult = await RecognizeIntentAsync(nlpAnalysis, context, cancellationToken);

                // 5. Parametre çıkarımı;
                var extractedParameters = await ExtractParametersAsync(nlpAnalysis, intentResult, cancellationToken);

                // 6. Kullanıcı bağlamını güncelle;
                UpdateUserContext(userId, nlpAnalysis, intentResult);

                // 7. Sonucu oluştur;
                var result = BuildProcessedResult(
                    rawInput,
                    preprocessedInput,
                    languageInfo,
                    nlpAnalysis,
                    intentResult,
                    extractedParameters,
                    userId;
                );

                _logger.LogInformation($"Input processed successfully. Intent: {intentResult.Intent}, Confidence: {intentResult.Confidence:F2}");

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Input processing was cancelled");
                throw;
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "NaturalInput",
                    Operation = "ProcessInput",
                    UserId = userId,
                    AdditionalData = new { Input = rawInput }
                });

                return await HandleProcessingErrorAsync(rawInput, ex, cancellationToken);
            }
            finally
            {
                _processingSemaphore.Release();
            }
        }

        /// <summary>
        /// Kullanıcının bağlamını temizler;
        /// </summary>
        public void ClearUserContext(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            lock (_userContexts)
            {
                if (_userContexts.ContainsKey(userId))
                {
                    _userContexts.Remove(userId);
                    _logger.LogInformation($"Cleared context for user: {userId}");
                }
            }
        }

        /// <summary>
        /// Kullanıcı bağlamını getirir;
        /// </summary>
        public UserInputContext GetUserContext(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return null;

            lock (_userContexts)
            {
                return _userContexts.TryGetValue(userId, out var context) ? context : null;
            }
        }

        /// <summary>
        /// Doğal dil modelini günceller;
        /// </summary>
        public async Task UpdateLanguageModelAsync(
            LanguageModelUpdate update,
            CancellationToken cancellationToken = default)
        {
            if (update == null)
                throw new ArgumentNullException(nameof(update));

            try
            {
                await _nlpEngine.UpdateModelAsync(update, cancellationToken);
                await _intentRecognizer.RetrainAsync(update.TrainingData, cancellationToken);

                _logger.LogInformation($"Language model updated successfully. Type: {update.UpdateType}");
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "NaturalInput",
                    Operation = "UpdateLanguageModel",
                    AdditionalData = new { UpdateType = update.UpdateType }
                });
                throw;
            }
        }

        /// <summary>
        /// Girdiyi ön işleme adımlarından geçirir;
        /// </summary>
        private async Task<string> PreprocessInputAsync(string input, CancellationToken cancellationToken)
        {
            var processed = input.Trim();

            // HTML/XML etiketlerini temizle;
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"<[^>]*>", string.Empty);

            // Çoklu boşlukları tek boşluğa indirge;
            processed = System.Text.RegularExpressions.Regex.Replace(processed, @"\s+", " ");

            // Özel karakterleri normalleştir;
            processed = processed.Replace("“", "\"")
                                 .Replace("”", "\"")
                                 .Replace("‘", "'")
                                 .Replace("’", "'")
                                 .Replace("…", "...");

            // Büyük/küçük harf normalizasyonu;
            if (_configuration.NormalizeCase)
                processed = processed.ToLowerInvariant();

            await Task.CompletedTask;
            return processed;
        }

        /// <summary>
        /// Girdinin dilini tespit eder;
        /// </summary>
        private async Task<LanguageDetectionResult> DetectLanguageAsync(string input, CancellationToken cancellationToken)
        {
            try
            {
                var detectionResult = await _nlpEngine.DetectLanguageAsync(input, cancellationToken);

                return new LanguageDetectionResult;
                {
                    LanguageCode = detectionResult.LanguageCode,
                    Confidence = detectionResult.Confidence,
                    IsReliable = detectionResult.Confidence > _configuration.MinLanguageConfidence;
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Language detection failed: {ex.Message}. Using default language.");

                return new LanguageDetectionResult;
                {
                    LanguageCode = _configuration.DefaultLanguage,
                    Confidence = 0.5f,
                    IsReliable = false;
                };
            }
        }

        /// <summary>
        /// Girdiyi token'lara ayırır;
        /// </summary>
        private async Task<TokenizationResult> TokenizeInputAsync(
            string input,
            LanguageDetectionResult languageInfo,
            CancellationToken cancellationToken)
        {
            var tokenizationOptions = new TokenizationOptions;
            {
                Language = languageInfo.LanguageCode,
                PreservePunctuation = _configuration.PreservePunctuation,
                SplitSentences = _configuration.SplitSentences;
            };

            return await _nlpEngine.TokenizeAsync(input, tokenizationOptions, cancellationToken);
        }

        /// <summary>
        /// NLP analizi gerçekleştirir;
        /// </summary>
        private async Task<NLPAnalysisResult> PerformNLPAnalysisAsync(
            TokenizationResult tokens,
            LanguageDetectionResult languageInfo,
            CancellationToken cancellationToken)
        {
            var analysisTasks = new List<Task>();
            var analysisResult = new NLPAnalysisResult;
            {
                Tokens = tokens,
                Language = languageInfo;
            };

            // Paralel analiz işlemleri;
            if (_configuration.EnableEntityRecognition)
            {
                analysisTasks.Add(Task.Run(async () =>
                {
                    analysisResult.Entities = await _nlpEngine.RecognizeEntitiesAsync(
                        tokens, languageInfo.LanguageCode, cancellationToken);
                }, cancellationToken));
            }

            if (_configuration.EnableSentimentAnalysis)
            {
                analysisTasks.Add(Task.Run(async () =>
                {
                    analysisResult.Sentiment = await _nlpEngine.AnalyzeSentimentAsync(
                        tokens, languageInfo.LanguageCode, cancellationToken);
                }, cancellationToken));
            }

            if (_configuration.EnableSyntaxAnalysis)
            {
                analysisTasks.Add(Task.Run(async () =>
                {
                    analysisResult.SyntaxTree = await _nlpEngine.ParseSyntaxAsync(
                        tokens, languageInfo.LanguageCode, cancellationToken);
                }, cancellationToken));
            }

            await Task.WhenAll(analysisTasks);
            return analysisResult;
        }

        /// <summary>
        /// Niyet tanıma işlemini gerçekleştirir;
        /// </summary>
        private async Task<IntentRecognitionResult> RecognizeIntentAsync(
            NLPAnalysisResult nlpAnalysis,
            InputContext context,
            CancellationToken cancellationToken)
        {
            var recognitionRequest = new IntentRecognitionRequest;
            {
                Tokens = nlpAnalysis.Tokens,
                Entities = nlpAnalysis.Entities,
                Sentiment = nlpAnalysis.Sentiment,
                Context = context,
                Language = nlpAnalysis.Language.LanguageCode;
            };

            return await _intentRecognizer.RecognizeAsync(recognitionRequest, cancellationToken);
        }

        /// <summary>
        /// Parametreleri çıkarır;
        /// </summary>
        private async Task<ExtractedParameters> ExtractParametersAsync(
            NLPAnalysisResult nlpAnalysis,
            IntentRecognitionResult intentResult,
            CancellationToken cancellationToken)
        {
            var extractionTasks = new List<Task<ParameterExtractionResult>>();

            foreach (var entity in nlpAnalysis.Entities)
            {
                extractionTasks.Add(_intentRecognizer.ExtractParametersAsync(
                    entity,
                    intentResult.Intent,
                    cancellationToken));
            }

            var extractionResults = await Task.WhenAll(extractionTasks);

            var parameters = new ExtractedParameters();
            foreach (var result in extractionResults)
            {
                parameters.AddRange(result.Parameters);
            }

            return parameters;
        }

        /// <summary>
        /// Kullanıcı bağlamını günceller;
        /// </summary>
        private void UpdateUserContext(string userId, NLPAnalysisResult analysis, IntentRecognitionResult intent)
        {
            if (string.IsNullOrWhiteSpace(userId))
                return;

            lock (_userContexts)
            {
                if (!_userContexts.TryGetValue(userId, out var context))
                {
                    context = new UserInputContext(userId);
                    _userContexts[userId] = context;
                }

                context.LastInput = analysis.Tokens.OriginalText;
                context.LastIntent = intent.Intent;
                context.Timestamp = DateTime.UtcNow;
                context.InteractionCount++;

                if (analysis.Entities != null)
                {
                    foreach (var entity in analysis.Entities)
                    {
                        context.AddEntity(entity.Type, entity.Value);
                    }
                }
            }
        }

        /// <summary>
        /// İşlenmiş sonucu oluşturur;
        /// </summary>
        private ProcessedInputResult BuildProcessedResult(
            string rawInput,
            string processedInput,
            LanguageDetectionResult languageInfo,
            NLPAnalysisResult nlpAnalysis,
            IntentRecognitionResult intentResult,
            ExtractedParameters parameters,
            string userId)
        {
            return new ProcessedInputResult;
            {
                RawInput = rawInput,
                ProcessedInput = processedInput,
                Language = languageInfo.LanguageCode,
                LanguageConfidence = languageInfo.Confidence,
                Intent = intentResult.Intent,
                IntentConfidence = intentResult.Confidence,
                Entities = nlpAnalysis.Entities,
                Sentiment = nlpAnalysis.Sentiment,
                Parameters = parameters,
                Timestamp = DateTime.UtcNow,
                UserId = userId,
                IsSuccessful = intentResult.Confidence >= _configuration.MinConfidenceThreshold;
            };
        }

        /// <summary>
        /// İşleme hatası durumunda hata sonucu oluşturur;
        /// </summary>
        private async Task<ProcessedInputResult> HandleProcessingErrorAsync(
            string input,
            Exception exception,
            CancellationToken cancellationToken)
        {
            // Fallback işleme: basit anahtar kelime analizi;
            var fallbackIntent = await PerformFallbackAnalysisAsync(input, cancellationToken);

            return new ProcessedInputResult;
            {
                RawInput = input,
                ProcessedInput = input,
                Intent = fallbackIntent,
                IntentConfidence = 0.3f, // Düşük güven skoru;
                IsSuccessful = false,
                Error = exception.Message,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Fallback analizi gerçekleştirir;
        /// </summary>
        private async Task<string> PerformFallbackAnalysisAsync(string input, CancellationToken cancellationToken)
        {
            // Basit anahtar kelime eşleştirmesi;
            var keywords = new Dictionary<string, string[]>
            {
                ["create"] = new[] { "oluştur", "yarat", "yap", "create", "make", "build" },
                ["delete"] = new[] { "sil", "kaldır", "delete", "remove" },
                ["modify"] = new[] { "değiştir", "düzenle", "modify", "edit", "update" },
                ["search"] = new[] { "ara", "bul", "search", "find" }
            };

            var inputLower = input.ToLowerInvariant();

            foreach (var kvp in keywords)
            {
                foreach (var keyword in kvp.Value)
                {
                    if (inputLower.Contains(keyword))
                    {
                        await Task.CompletedTask;
                        return kvp.Key;
                    }
                }
            }

            return "unknown";
        }

        #region IDisposable Implementation;
        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _processingSemaphore?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
        #endregion;
    }

    /// <summary>
    /// Doğal dil girdi işleme konfigürasyonu;
    /// </summary>
    public class NaturalInputConfiguration;
    {
        public static NaturalInputConfiguration Default => new NaturalInputConfiguration();

        public int MaxConcurrentProcesses { get; set; } = 10;
        public float MinConfidenceThreshold { get; set; } = 0.7f;
        public float MinLanguageConfidence { get; set; } = 0.8f;
        public string DefaultLanguage { get; set; } = "en-US";
        public bool NormalizeCase { get; set; } = true;
        public bool PreservePunctuation { get; set; } = true;
        public bool SplitSentences { get; set; } = true;
        public bool EnableEntityRecognition { get; set; } = true;
        public bool EnableSentimentAnalysis { get; set; } = true;
        public bool EnableSyntaxAnalysis { get; set; } = true;
        public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// İşlenmiş girdi sonucu;
    /// </summary>
    public class ProcessedInputResult;
    {
        public string RawInput { get; set; }
        public string ProcessedInput { get; set; }
        public string Language { get; set; }
        public float LanguageConfidence { get; set; }
        public string Intent { get; set; }
        public float IntentConfidence { get; set; }
        public IEnumerable<RecognizedEntity> Entities { get; set; }
        public SentimentAnalysisResult Sentiment { get; set; }
        public ExtractedParameters Parameters { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; }
        public bool IsSuccessful { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Kullanıcı girdi bağlamı;
    /// </summary>
    public class UserInputContext;
    {
        public string UserId { get; }
        public string LastInput { get; set; }
        public string LastIntent { get; set; }
        public DateTime Timestamp { get; set; }
        public int InteractionCount { get; set; }
        private readonly Dictionary<string, List<string>> _entityHistory;

        public UserInputContext(string userId)
        {
            UserId = userId ?? throw new ArgumentNullException(nameof(userId));
            _entityHistory = new Dictionary<string, List<string>>();
            Timestamp = DateTime.UtcNow;
        }

        public void AddEntity(string type, string value)
        {
            if (!_entityHistory.ContainsKey(type))
                _entityHistory[type] = new List<string>();

            _entityHistory[type].Add(value);
        }

        public IEnumerable<string> GetEntityHistory(string type)
        {
            return _entityHistory.TryGetValue(type, out var history)
                ? history.AsReadOnly()
                : Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// Dil tespit sonucu;
    /// </summary>
    public class LanguageDetectionResult;
    {
        public string LanguageCode { get; set; }
        public float Confidence { get; set; }
        public bool IsReliable { get; set; }
    }

    /// <summary>
    /// NLP analiz sonucu;
    /// </summary>
    public class NLPAnalysisResult;
    {
        public TokenizationResult Tokens { get; set; }
        public LanguageDetectionResult Language { get; set; }
        public IEnumerable<RecognizedEntity> Entities { get; set; }
        public SentimentAnalysisResult Sentiment { get; set; }
        public SyntaxTree SyntaxTree { get; set; }
    }

    /// <summary>
    /// Girdi bağlamı;
    /// </summary>
    public class InputContext;
    {
        public string Domain { get; set; }
        public string PreviousIntent { get; set; }
        public Dictionary<string, object> SessionData { get; set; }
        public DeviceInfo Device { get; set; }
    }

    /// <summary>
    /// Dil modeli güncellemesi;
    /// </summary>
    public class LanguageModelUpdate;
    {
        public UpdateType UpdateType { get; set; }
        public object TrainingData { get; set; }
        public string Language { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Cihaz bilgisi;
    /// </summary>
    public class DeviceInfo;
    {
        public string Type { get; set; }
        public string Platform { get; set; }
        public string Version { get; set; }
        public string Location { get; set; }
    }

    /// <summary>
    /// Güncelleme türleri;
    /// </summary>
    public enum UpdateType;
    {
        Incremental,
        Full,
        Patch,
        Custom;
    }
}

namespace NEDA.Interface.TextInput.NaturalLanguageInput.Contracts;
{
    /// <summary>
    /// Doğal dil girdi işleme interface'i;
    /// </summary>
    public interface INaturalInput : IDisposable
    {
        /// <summary>
        /// Doğal dil girdisini işler;
        /// </summary>
        Task<ProcessedInputResult> ProcessInputAsync(
            string rawInput,
            string userId = null,
            InputContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Kullanıcı bağlamını temizler;
        /// </summary>
        void ClearUserContext(string userId);

        /// <summary>
        /// Kullanıcı bağlamını getirir;
        /// </summary>
        UserInputContext GetUserContext(string userId);

        /// <summary>
        /// Dil modelini günceller;
        /// </summary>
        Task UpdateLanguageModelAsync(
            LanguageModelUpdate update,
            CancellationToken cancellationToken = default);
    }
}
