using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Logging;
using NEDA.Brain.NLP_Engine;
using NEDA.Brain.MemorySystem;
using NEDA.KnowledgeBase.DataManagement.Repositories;
using NEDA.Communication.DialogSystem.QuestionAnswering;

namespace NEDA.Communication.DialogSystem.QuestionAnswering;
{
    /// <summary>
    /// Soru-Cevap sistemi için ana motor sınıfı;
    /// Doğal dildeki soruları analiz eder ve uygun cevapları üretir;
    /// </summary>
    public interface IQASystem : IDisposable
    {
        /// <summary>
        /// Soruya cevap verir;
        /// </summary>
        Task<QAAnswer> AnswerQuestionAsync(QuestionRequest request);

        /// <summary>
        /// Çoklu sorulara toplu cevap verir;
        /// </summary>
        Task<IEnumerable<QAAnswer>> AnswerBatchQuestionsAsync(IEnumerable<QuestionRequest> requests);

        /// <summary>
        /// QA sistemini eğitir;
        /// </summary>
        Task<TrainResult> TrainAsync(TrainingData data);

        /// <summary>
        /// Sistem durumunu kontrol eder;
        /// </summary>
        Task<SystemHealth> CheckHealthAsync();

        /// <summary>
        /// QA performans metriklerini getirir;
        /// </summary>
        Task<QAMetrics> GetMetricsAsync();
    }

    /// <summary>
    /// Soru-Cevap sistemi implementasyonu;
    /// </summary>
    public class QASystem : IQASystem;
    {
        private readonly ILogger<QASystem> _logger;
        private readonly INLPEngine _nlpEngine;
        private readonly IKnowledgeRepository _knowledgeRepository;
        private readonly IMemoryRecall _memoryRecall;
        private readonly IQAPipeline _qaPipeline;
        private readonly IAnswerEngine _answerEngine;
        private readonly IQueryProcessor _queryProcessor;
        private bool _disposed;

        /// <summary>
        /// QA sistemi için constructor;
        /// </summary>
        public QASystem(
            ILogger<QASystem> logger,
            INLPEngine nlpEngine,
            IKnowledgeRepository knowledgeRepository,
            IMemoryRecall memoryRecall,
            IQAPipeline qaPipeline,
            IAnswerEngine answerEngine,
            IQueryProcessor queryProcessor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _nlpEngine = nlpEngine ?? throw new ArgumentNullException(nameof(nlpEngine));
            _knowledgeRepository = knowledgeRepository ?? throw new ArgumentNullException(nameof(knowledgeRepository));
            _memoryRecall = memoryRecall ?? throw new ArgumentNullException(nameof(memoryRecall));
            _qaPipeline = qaPipeline ?? throw new ArgumentNullException(nameof(qaPipeline));
            _answerEngine = answerEngine ?? throw new ArgumentNullException(nameof(answerEngine));
            _queryProcessor = queryProcessor ?? throw new ArgumentNullException(nameof(queryProcessor));

            _logger.LogInformation("QASystem initialized");
        }

        /// <summary>
        /// Soruya cevap verme işlemi;
        /// </summary>
        public async Task<QAAnswer> AnswerQuestionAsync(QuestionRequest request)
        {
            try
            {
                ValidateRequest(request);

                _logger.LogInformation("Processing question: {Question}", request.Question);

                // 1. Soruyu analiz et;
                var analyzedQuestion = await AnalyzeQuestionAsync(request);

                // 2. Query'i işle;
                var processedQuery = await ProcessQueryAsync(analyzedQuestion);

                // 3. Bilgiyi ara;
                var knowledgeResult = await SearchKnowledgeAsync(processedQuery);

                // 4. Hafızayı kontrol et;
                var memoryResult = await SearchMemoryAsync(processedQuery);

                // 5. Cevabı oluştur;
                var answer = await GenerateAnswerAsync(
                    analyzedQuestion,
                    processedQuery,
                    knowledgeResult,
                    memoryResult);

                // 6. Cevabı formatla;
                var formattedAnswer = await FormatAnswerAsync(answer, request);

                _logger.LogInformation("Question answered successfully. Confidence: {Confidence}",
                    formattedAnswer.Confidence);

                return formattedAnswer;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error answering question: {Question}", request.Question);
                return await GenerateErrorAnswerAsync(request, ex);
            }
        }

        /// <summary>
        /// Toplu soru-cevap işlemi;
        /// </summary>
        public async Task<IEnumerable<QAAnswer>> AnswerBatchQuestionsAsync(IEnumerable<QuestionRequest> requests)
        {
            var batchAnswers = new List<QAAnswer>();
            var tasks = new List<Task<QAAnswer>>();

            foreach (var request in requests)
            {
                tasks.Add(AnswerQuestionAsync(request));
            }

            var results = await Task.WhenAll(tasks);
            return results;
        }

        /// <summary>
        /// QA sistemini eğitir;
        /// </summary>
        public async Task<TrainResult> TrainAsync(TrainingData data)
        {
            try
            {
                _logger.LogInformation("Starting QA system training with {Count} samples", data.Samples.Count);

                var results = new List<TrainingSampleResult>();

                foreach (var sample in data.Samples)
                {
                    try
                    {
                        // Eğitim verisini işle;
                        await ProcessTrainingSampleAsync(sample);
                        results.Add(new TrainingSampleResult;
                        {
                            SampleId = sample.Id,
                            Success = true;
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing training sample {Id}", sample.Id);
                        results.Add(new TrainingSampleResult;
                        {
                            SampleId = sample.Id,
                            Success = false,
                            Error = ex.Message;
                        });
                    }
                }

                // Modeli güncelle;
                await UpdateModelAsync(data);

                // Performansı değerlendir;
                var metrics = await EvaluateTrainingPerformanceAsync(data);

                _logger.LogInformation("QA training completed successfully");

                return new TrainResult;
                {
                    Success = true,
                    ProcessedSamples = results.Count(r => r.Success),
                    FailedSamples = results.Count(r => !r.Success),
                    Metrics = metrics;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during QA system training");
                throw new QATrainingException("QA system training failed", ex);
            }
        }

        /// <summary>
        /// Sistem sağlık kontrolü;
        /// </summary>
        public async Task<SystemHealth> CheckHealthAsync()
        {
            try
            {
                var healthChecks = new List<HealthCheckResult>();

                // NLP Engine sağlık kontrolü;
                healthChecks.Add(await CheckNLPEngineHealthAsync());

                // Knowledge Repository sağlık kontrolü;
                healthChecks.Add(await CheckKnowledgeRepositoryHealthAsync());

                // Memory Recall sağlık kontrolü;
                healthChecks.Add(await CheckMemoryRecallHealthAsync());

                // Pipeline sağlık kontrolü;
                healthChecks.Add(await CheckPipelineHealthAsync());

                var overallHealth = healthChecks.All(h => h.IsHealthy)
                    ? HealthStatus.Healthy;
                    : HealthStatus.Unhealthy;

                return new SystemHealth;
                {
                    Status = overallHealth,
                    ComponentHealth = healthChecks,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed");
                return new SystemHealth;
                {
                    Status = HealthStatus.Unhealthy,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        QA performans metriklerini getirir;
        /// </summary>
        public async Task<QAMetrics> GetMetricsAsync()
        {
            try
            {
                var metrics = new QAMetrics;
                {
                    TotalQuestionsProcessed = await GetTotalQuestionsCountAsync(),
                    AverageResponseTime = await GetAverageResponseTimeAsync(),
                    SuccessRate = await GetSuccessRateAsync(),
                    AccuracyScore = await GetAccuracyScoreAsync(),
                    LastUpdated = DateTime.UtcNow;
                };

                // Detaylı metrikler;
                metrics.DetailedMetrics = await GetDetailedMetricsAsync();

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting QA metrics");
                throw new QAMetricsException("Failed to retrieve QA metrics", ex);
            }
        }

        /// <summary>
        /// Soruyu analiz eder;
        /// </summary>
        private async Task<AnalyzedQuestion> AnalyzeQuestionAsync(QuestionRequest request)
        {
            var analysis = await _nlpEngine.AnalyzeAsync(request.Question, request.Context);

            return new AnalyzedQuestion;
            {
                OriginalText = request.Question,
                Tokens = analysis.Tokens,
                Entities = analysis.Entities,
                Intent = analysis.Intent,
                Sentiment = analysis.Sentiment,
                Language = analysis.Language,
                IsComplex = analysis.Complexity > 0.7,
                RequiresClarification = analysis.Clarity < 0.5;
            };
        }

        /// <summary>
        /// Query'i işler;
        /// </summary>
        private async Task<ProcessedQuery> ProcessQueryAsync(AnalyzedQuestion question)
        {
            return await _queryProcessor.ProcessAsync(new QueryRequest;
            {
                AnalyzedQuestion = question,
                MaxResults = 10,
                IncludeSources = true,
                SearchDepth = SearchDepth.Deep;
            });
        }

        /// <summary>
        /// Bilgi tabanında ara;
        /// </summary>
        private async Task<KnowledgeSearchResult> SearchKnowledgeAsync(ProcessedQuery query)
        {
            var searchRequest = new KnowledgeSearchRequest;
            {
                Query = query.ProcessedText,
                Filters = query.Filters,
                MaxResults = query.MaxResults,
                RelevanceThreshold = 0.6;
            };

            return await _knowledgeRepository.SearchAsync(searchRequest);
        }

        /// <summary>
        /// Hafızada ara;
        /// </summary>
        private async Task<MemorySearchResult> SearchMemoryAsync(ProcessedQuery query)
        {
            var memoryRequest = new MemoryRecallRequest;
            {
                Query = query.ProcessedText,
                Context = query.Context,
                MemoryType = MemoryType.Both,
                RelevanceThreshold = 0.5;
            };

            return await _memoryRecall.RecallAsync(memoryRequest);
        }

        /// <summary>
        /// Cevap oluştur;
        /// </summary>
        private async Task<GeneratedAnswer> GenerateAnswerAsync(
            AnalyzedQuestion question,
            ProcessedQuery query,
            KnowledgeSearchResult knowledge,
            MemorySearchResult memory)
        {
            var answerRequest = new AnswerGenerationRequest;
            {
                Question = question,
                Query = query,
                KnowledgeResults = knowledge.Results,
                MemoryResults = memory.Results,
                AnswerStyle = DetermineAnswerStyle(question),
                DetailLevel = DetermineDetailLevel(question)
            };

            return await _answerEngine.GenerateAsync(answerRequest);
        }

        /// <summary>
        /// Cevabı formatla;
        /// </summary>
        private async Task<QAAnswer> FormatAnswerAsync(GeneratedAnswer answer, QuestionRequest request)
        {
            var formatted = await _qaPipeline.FormatAsync(answer, request.Format);

            return new QAAnswer;
            {
                Answer = formatted.Text,
                Confidence = formatted.Confidence,
                Sources = formatted.Sources,
                SuggestedFollowUps = formatted.FollowUpQuestions,
                ExecutionTime = formatted.ProcessingTime,
                IsComplete = formatted.IsComplete,
                RequiresFollowUp = formatted.RequiresFollowUp,
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Hata durumunda cevap oluştur;
        /// </summary>
        private async Task<QAAnswer> GenerateErrorAnswerAsync(QuestionRequest request, Exception ex)
        {
            return new QAAnswer;
            {
                Answer = "Üzgünüm, bu soruyu cevaplamakta zorlanıyorum. Lütfen farklı bir şekilde sorun veya daha sonra tekrar deneyin.",
                Confidence = 0.0,
                IsComplete = false,
                Error = new QASystemError;
                {
                    Code = "QA_ANSWER_ERROR",
                    Message = ex.Message,
                    Details = ex.ToString()
                },
                Timestamp = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Eğitim örneğini işle;
        /// </summary>
        private async Task ProcessTrainingSampleAsync(TrainingSample sample)
        {
            // Örnek analizi;
            var analysis = await _nlpEngine.AnalyzeAsync(sample.Question, sample.Context);

            // Knowledge base'e ekle;
            await _knowledgeRepository.AddAsync(new KnowledgeItem;
            {
                Id = Guid.NewGuid().ToString(),
                Content = sample.CorrectAnswer,
                Metadata = new Dictionary<string, object>
                {
                    ["question"] = sample.Question,
                    ["context"] = sample.Context,
                    ["category"] = sample.Category,
                    ["difficulty"] = sample.Difficulty;
                },
                Tags = sample.Tags,
                CreatedAt = DateTime.UtcNow;
            });

            // Memory'e kaydet;
            await _memoryRecall.StoreAsync(new MemoryItem;
            {
                Content = sample.Question + " " + sample.CorrectAnswer,
                Type = MemoryType.LongTerm,
                Tags = new[] { "training", sample.Category }
            });
        }

        /// <summary>
        /// Modeli güncelle;
        /// </summary>
        private async Task UpdateModelAsync(TrainingData data)
        {
            // Model güncelleme mantığı;
            await Task.Delay(100); // Simüle edilmiş işlem;

            _logger.LogInformation("QA model updated with {Count} training samples", data.Samples.Count);
        }

        /// <summary>
        /// Eğitim performansını değerlendir;
        /// </summary>
        private async Task<TrainingMetrics> EvaluateTrainingPerformanceAsync(TrainingData data)
        {
            // Performans değerlendirme mantığı;
            var testResults = new List<TestResult>();

            foreach (var sample in data.TestSamples)
            {
                var answer = await AnswerQuestionAsync(new QuestionRequest;
                {
                    Question = sample.Question,
                    Context = sample.Context;
                });

                testResults.Add(new TestResult;
                {
                    SampleId = sample.Id,
                    Correct = EvaluateAnswerCorrectness(answer.Answer, sample.CorrectAnswer),
                    Confidence = answer.Confidence;
                });
            }

            return new TrainingMetrics;
            {
                Accuracy = testResults.Count(r => r.Correct) / (double)testResults.Count,
                AverageConfidence = testResults.Average(r => r.Confidence),
                TotalTested = testResults.Count;
            };
        }

        /// <summary>
        /// Cevap doğruluğunu değerlendir;
        /// </summary>
        private bool EvaluateAnswerCorrectness(string generatedAnswer, string correctAnswer)
        {
            // Basit bir benzerlik kontrolü (gerçek implementasyonda daha gelişmiş NLP kullanılır)
            var similarity = CalculateSimilarity(generatedAnswer, correctAnswer);
            return similarity > 0.7;
        }

        /// <summary>
        /// Metin benzerliğini hesapla;
        /// </summary>
        private double CalculateSimilarity(string text1, string text2)
        {
            // Basit benzerlik hesaplama;
            var words1 = text1.ToLower().Split(' ').Distinct().ToList();
            var words2 = text2.ToLower().Split(' ').Distinct().ToList();

            var intersection = words1.Intersect(words2).Count();
            var union = words1.Union(words2).Count();

            return union > 0 ? (double)intersection / union : 0;
        }

        /// <summary>
        /// Cevap stilini belirle;
        /// </summary>
        private AnswerStyle DetermineAnswerStyle(AnalyzedQuestion question)
        {
            if (question.Sentiment == Sentiment.Negative)
                return AnswerStyle.Empathetic;

            if (question.IsComplex)
                return AnswerStyle.Detailed;

            return AnswerStyle.Concise;
        }

        /// <summary>
        /// Detay seviyesini belirle;
        /// </summary>
        private DetailLevel DetermineDetailLevel(AnalyzedQuestion question)
        {
            if (question.RequiresClarification)
                return DetailLevel.Basic;

            if (question.IsComplex)
                return DetailLevel.Advanced;

            return DetailLevel.Normal;
        }

        /// <summary>
        /// NLP Engine sağlık kontrolü;
        /// </summary>
        private async Task<HealthCheckResult> CheckNLPEngineHealthAsync()
        {
            try
            {
                var isHealthy = await _nlpEngine.CheckHealthAsync();
                return new HealthCheckResult;
                {
                    Component = "NLPEngine",
                    IsHealthy = isHealthy,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult;
                {
                    Component = "NLPEngine",
                    IsHealthy = false,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Knowledge Repository sağlık kontrolü;
        /// </summary>
        private async Task<HealthCheckResult> CheckKnowledgeRepositoryHealthAsync()
        {
            try
            {
                var isHealthy = await _knowledgeRepository.CheckConnectionAsync();
                return new HealthCheckResult;
                {
                    Component = "KnowledgeRepository",
                    IsHealthy = isHealthy,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult;
                {
                    Component = "KnowledgeRepository",
                    IsHealthy = false,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Memory Recall sağlık kontrolü;
        /// </summary>
        private async Task<HealthCheckResult> CheckMemoryRecallHealthAsync()
        {
            try
            {
                var health = await _memoryRecall.CheckHealthAsync();
                return new HealthCheckResult;
                {
                    Component = "MemoryRecall",
                    IsHealthy = health.IsHealthy,
                    Details = health.Details,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult;
                {
                    Component = "MemoryRecall",
                    IsHealthy = false,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// Pipeline sağlık kontrolü;
        /// </summary>
        private async Task<HealthCheckResult> CheckPipelineHealthAsync()
        {
            try
            {
                var isHealthy = await _qaPipeline.CheckHealthAsync();
                return new HealthCheckResult;
                {
                    Component = "QAPipeline",
                    IsHealthy = isHealthy,
                    Timestamp = DateTime.UtcNow;
                };
            }
            catch (Exception ex)
            {
                return new HealthCheckResult;
                {
                    Component = "QAPipeline",
                    IsHealthy = false,
                    Error = ex.Message,
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        /// <summary>
        /// İşlenen toplam soru sayısını getir;
        /// </summary>
        private async Task<long> GetTotalQuestionsCountAsync()
        {
            // Gerçek implementasyonda veritabanından alınır;
            return await Task.FromResult(1000L);
        }

        /// <summary>
        /// Ortalama cevap süresini getir;
        /// </summary>
        private async Task<TimeSpan> GetAverageResponseTimeAsync()
        {
            // Gerçek implementasyonda metriklerden alınır;
            return await Task.FromResult(TimeSpan.FromMilliseconds(350));
        }

        /// <summary>
        /// Başarı oranını getir;
        /// </summary>
        private async Task<double> GetSuccessRateAsync()
        {
            // Gerçek implementasyonda metriklerden alınır;
            return await Task.FromResult(0.92);
        }

        /// <summary>
        /// Doğruluk skorunu getir;
        /// </summary>
        private async Task<double> GetAccuracyScoreAsync()
        {
            // Gerçek implementasyonda test sonuçlarından hesaplanır;
            return await Task.FromResult(0.88);
        }

        /// <summary>
        /// Detaylı metrikleri getir;
        /// </summary>
        private async Task<DetailedMetrics> GetDetailedMetricsAsync()
        {
            return await Task.FromResult(new DetailedMetrics;
            {
                QuestionsByCategory = new Dictionary<string, int>
                {
                    ["general"] = 400,
                    ["technical"] = 300,
                    ["howto"] = 200,
                    ["definition"] = 100;
                },
                AverageConfidenceByCategory = new Dictionary<string, double>
                {
                    ["general"] = 0.95,
                    ["technical"] = 0.85,
                    ["howto"] = 0.90,
                    ["definition"] = 0.98;
                },
                MostCommonQuestions = new List<string>
                {
                    "Nasıl yapılır?",
                    "Bu nedir?",
                    "Neden hata alıyorum?"
                }
            });
        }

        /// <summary>
        /// Request validasyonu;
        /// </summary>
        private void ValidateRequest(QuestionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (string.IsNullOrWhiteSpace(request.Question))
                throw new ArgumentException("Question cannot be empty", nameof(request.Question));

            if (request.Question.Length > 1000)
                throw new ArgumentException("Question is too long", nameof(request.Question));
        }

        /// <summary>
        /// Dispose pattern implementasyonu;
        /// </summary>
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
                    _logger.LogInformation("QASystem disposing");
                }

                _disposed = true;
            }
        }

        ~QASystem()
        {
            Dispose(false);
        }
    }

    #region Data Models;

    /// <summary>
    /// Soru isteği modeli;
    /// </summary>
    public class QuestionRequest;
    {
        public string Question { get; set; }
        public string Context { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public AnswerFormat Format { get; set; } = AnswerFormat.Text;
        public Language Language { get; set; } = Language.Turkish;
        public int MaxAnswerLength { get; set; } = 500;
    }

    /// <summary>
    /// QA cevap modeli;
    /// </summary>
    public class QAAnswer;
    {
        public string Answer { get; set; }
        public double Confidence { get; set; }
        public List<string> Sources { get; set; } = new List<string>();
        public List<string> SuggestedFollowUps { get; set; } = new List<string>();
        public TimeSpan ExecutionTime { get; set; }
        public bool IsComplete { get; set; }
        public bool RequiresFollowUp { get; set; }
        public QASystemError Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Eğitim verisi modeli;
    /// </summary>
    public class TrainingData;
    {
        public List<TrainingSample> Samples { get; set; } = new List<TrainingSample>();
        public List<TrainingSample> TestSamples { get; set; } = new List<TrainingSample>();
        public string ModelVersion { get; set; }
        public DateTime TrainingDate { get; set; }
    }

    /// <summary>
    /// Eğitim örneği modeli;
    /// </summary>
    public class TrainingSample;
    {
        public string Id { get; set; }
        public string Question { get; set; }
        public string CorrectAnswer { get; set; }
        public string Context { get; set; }
        public string Category { get; set; }
        public DifficultyLevel Difficulty { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
    }

    /// <summary>
    /// Sistem sağlık modeli;
    /// </summary>
    public class SystemHealth;
    {
        public HealthStatus Status { get; set; }
        public List<HealthCheckResult> ComponentHealth { get; set; } = new List<HealthCheckResult>();
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// QA metrikleri modeli;
    /// </summary>
    public class QAMetrics;
    {
        public long TotalQuestionsProcessed { get; set; }
        public TimeSpan AverageResponseTime { get; set; }
        public double SuccessRate { get; set; }
        public double AccuracyScore { get; set; }
        public DetailedMetrics DetailedMetrics { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Analiz edilmiş soru modeli;
    /// </summary>
    public class AnalyzedQuestion;
    {
        public string OriginalText { get; set; }
        public List<string> Tokens { get; set; }
        public List<Entity> Entities { get; set; }
        public string Intent { get; set; }
        public Sentiment Sentiment { get; set; }
        public string Language { get; set; }
        public bool IsComplex { get; set; }
        public bool RequiresClarification { get; set; }
    }

    /// <summary>
    /// Üretilen cevap modeli;
    /// </summary>
    public class GeneratedAnswer;
    {
        public string RawAnswer { get; set; }
        public double Confidence { get; set; }
        public List<SourceReference> Sources { get; set; }
        public AnswerStyle Style { get; set; }
        public DetailLevel DetailLevel { get; set; }
        public bool IsVerified { get; set; }
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Cevap formatı;
    /// </summary>
    public enum AnswerFormat;
    {
        Text,
        Html,
        Markdown,
        Json;
    }

    /// <summary>
    /// Dil;
    /// </summary>
    public enum Language;
    {
        Turkish,
        English,
        Auto;
    }

    /// <summary>
    /// Sağlık durumu;
    /// </summary>
    public enum HealthStatus;
    {
        Healthy,
        Degraded,
        Unhealthy;
    }

    /// <summary>
    /// Zorluk seviyesi;
    /// </summary>
    public enum DifficultyLevel;
    {
        Easy,
        Medium,
        Hard,
        Expert;
    }

    /// <summary>
    /// Arama derinliği;
    /// </summary>
    public enum SearchDepth;
    {
        Shallow,
        Normal,
        Deep;
    }

    /// <summary>
    /// Hafıza tipi;
    /// </summary>
    public enum MemoryType;
    {
        ShortTerm,
        LongTerm,
        Both;
    }

    /// <summary>
    /// Duygu;
    /// </summary>
    public enum Sentiment;
    {
        Positive,
        Neutral,
        Negative,
        Mixed;
    }

    /// <summary>
    /// Cevap stili;
    /// </summary>
    public enum AnswerStyle;
    {
        Concise,
        Detailed,
        Empathetic,
        Technical;
    }

    /// <summary>
    /// Detay seviyesi;
    /// </summary>
    public enum DetailLevel;
    {
        Basic,
        Normal,
        Advanced,
        Expert;
    }

    #endregion;

    #region Helper Classes;

    /// <summary>
    /// QA hata modeli;
    /// </summary>
    public class QASystemError;
    {
        public string Code { get; set; }
        public string Message { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Eğitim sonucu;
    /// </summary>
    public class TrainResult;
    {
        public bool Success { get; set; }
        public int ProcessedSamples { get; set; }
        public int FailedSamples { get; set; }
        public TrainingMetrics Metrics { get; set; }
    }

    /// <summary>
    /// Eğitim örneği sonucu;
    /// </summary>
    public class TrainingSampleResult;
    {
        public string SampleId { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// Eğitim metrikleri;
    /// </summary>
    public class TrainingMetrics;
    {
        public double Accuracy { get; set; }
        public double AverageConfidence { get; set; }
        public int TotalTested { get; set; }
    }

    /// <summary>
    /// Test sonucu;
    /// </summary>
    public class TestResult;
    {
        public string SampleId { get; set; }
        public bool Correct { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Sağlık kontrolü sonucu;
    /// </summary>
    public class HealthCheckResult;
    {
        public string Component { get; set; }
        public bool IsHealthy { get; set; }
        public string Error { get; set; }
        public string Details { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Detaylı metrikler;
    /// </summary>
    public class DetailedMetrics;
    {
        public Dictionary<string, int> QuestionsByCategory { get; set; }
        public Dictionary<string, double> AverageConfidenceByCategory { get; set; }
        public List<string> MostCommonQuestions { get; set; }
    }

    /// <summary>
    /// Varlık modeli;
    /// </summary>
    public class Entity;
    {
        public string Text { get; set; }
        public string Type { get; set; }
        public double Confidence { get; set; }
    }

    /// <summary>
    /// Kaynak referansı;
    /// </summary>
    public class SourceReference;
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public double Relevance { get; set; }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// QA sistemi özel exception;
    /// </summary>
    public class QASystemException : Exception
    {
        public string ErrorCode { get; }

        public QASystemException(string message) : base(message)
        {
            ErrorCode = "QA_SYSTEM_ERROR";
        }

        public QASystemException(string message, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = "QA_SYSTEM_ERROR";
        }

        public QASystemException(string errorCode, string message) : base(message)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// QA eğitimi exception;
    /// </summary>
    public class QATrainingException : QASystemException;
    {
        public QATrainingException(string message) : base("QA_TRAINING_ERROR", message)
        {
        }

        public QATrainingException(string message, Exception innerException)
            : base("QA_TRAINING_ERROR", message, innerException)
        {
        }
    }

    /// <summary>
    /// QA metrikleri exception;
    /// </summary>
    public class QAMetricsException : QASystemException;
    {
        public QAMetricsException(string message) : base("QA_METRICS_ERROR", message)
        {
        }

        public QAMetricsException(string message, Exception innerException)
            : base("QA_METRICS_ERROR", message, innerException)
        {
        }
    }

    #endregion;

    #region Dependency Interfaces;

    /// <summary>
    /// NLP Engine interface'i;
    /// </summary>
    public interface INLPEngine;
    {
        Task<AnalysisResult> AnalyzeAsync(string text, string context);
        Task<bool> CheckHealthAsync();
    }

    /// <summary>
    /// Bilgi deposu interface'i;
    /// </summary>
    public interface IKnowledgeRepository;
    {
        Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request);
        Task AddAsync(KnowledgeItem item);
        Task<bool> CheckConnectionAsync();
    }

    /// <summary>
    /// Hafıza recall interface'i;
    /// </summary>
    public interface IMemoryRecall;
    {
        Task<MemorySearchResult> RecallAsync(MemoryRecallRequest request);
        Task StoreAsync(MemoryItem item);
        Task<MemoryHealth> CheckHealthAsync();
    }

    /// <summary>
    /// QA Pipeline interface'i;
    /// </summary>
    public interface IQAPipeline;
    {
        Task<FormattedAnswer> FormatAsync(GeneratedAnswer answer, AnswerFormat format);
        Task<bool> CheckHealthAsync();
    }

    /// <summary>
    /// Cevap engine interface'i;
    /// </summary>
    public interface IAnswerEngine;
    {
        Task<GeneratedAnswer> GenerateAsync(AnswerGenerationRequest request);
    }

    /// <summary>
    /// Query processor interface'i;
    /// </summary>
    public interface IQueryProcessor;
    {
        Task<ProcessedQuery> ProcessAsync(QueryRequest request);
    }

    #endregion;

    #region Dependency Models;

    /// <summary>
    /// NLP analiz sonucu;
    /// </summary>
    public class AnalysisResult;
    {
        public List<string> Tokens { get; set; }
        public List<Entity> Entities { get; set; }
        public string Intent { get; set; }
        public Sentiment Sentiment { get; set; }
        public double Clarity { get; set; }
        public double Complexity { get; set; }
        public string Language { get; set; }
    }

    /// <summary>
    /// Bilgi arama isteği;
    /// </summary>
    public class KnowledgeSearchRequest;
    {
        public string Query { get; set; }
        public Dictionary<string, object> Filters { get; set; }
        public int MaxResults { get; set; }
        public double RelevanceThreshold { get; set; }
    }

    /// <summary>
    /// Bilgi arama sonucu;
    /// </summary>
    public class KnowledgeSearchResult;
    {
        public List<KnowledgeItem> Results { get; set; }
        public int TotalFound { get; set; }
        public TimeSpan SearchTime { get; set; }
    }

    /// <summary>
    /// Bilgi öğesi;
    /// </summary>
    public class KnowledgeItem;
    {
        public string Id { get; set; }
        public string Content { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public List<string> Tags { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Hafıza recall isteği;
    /// </summary>
    public class MemoryRecallRequest;
    {
        public string Query { get; set; }
        public string Context { get; set; }
        public MemoryType MemoryType { get; set; }
        public double RelevanceThreshold { get; set; }
    }

    /// <summary>
    /// Hafıza arama sonucu;
    /// </summary>
    public class MemorySearchResult;
    {
        public List<MemoryItem> Results { get; set; }
        public double AverageRelevance { get; set; }
    }

    /// <summary>
    /// Hafıza öğesi;
    /// </summary>
    public class MemoryItem;
    {
        public string Content { get; set; }
        public MemoryType Type { get; set; }
        public string[] Tags { get; set; }
        public DateTime AccessedAt { get; set; }
    }

    /// <summary>
    /// Hafıza sağlığı;
    /// </summary>
    public class MemoryHealth;
    {
        public bool IsHealthy { get; set; }
        public string Details { get; set; }
    }

    /// <summary>
    /// Query isteği;
    /// </summary>
    public class QueryRequest;
    {
        public AnalyzedQuestion AnalyzedQuestion { get; set; }
        public int MaxResults { get; set; }
        public bool IncludeSources { get; set; }
        public SearchDepth SearchDepth { get; set; }
    }

    /// <summary>
    /// İşlenmiş query;
    /// </summary>
    public class ProcessedQuery;
    {
        public string ProcessedText { get; set; }
        public Dictionary<string, object> Filters { get; set; }
        public string[] Keywords { get; set; }
        public int MaxResults { get; set; }
        public string Context { get; set; }
    }

    /// <summary>
    /// Cevap üretme isteği;
    /// </summary>
    public class AnswerGenerationRequest;
    {
        public AnalyzedQuestion Question { get; set; }
        public ProcessedQuery Query { get; set; }
        public List<KnowledgeItem> KnowledgeResults { get; set; }
        public List<MemoryItem> MemoryResults { get; set; }
        public AnswerStyle AnswerStyle { get; set; }
        public DetailLevel DetailLevel { get; set; }
    }

    /// <summary>
    /// Formatlanmış cevap;
    /// </summary>
    public class FormattedAnswer;
    {
        public string Text { get; set; }
        public double Confidence { get; set; }
        public List<string> Sources { get; set; }
        public List<string> FollowUpQuestions { get; set; }
        public TimeSpan ProcessingTime { get; set; }
        public bool IsComplete { get; set; }
        public bool RequiresFollowUp { get; set; }
    }

    #endregion;
}
