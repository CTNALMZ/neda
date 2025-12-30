using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign.Models;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign.Templates;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign.Validation;
using NEDA.Services.Messaging.EventBus;
using NEDA.AI.MachineLearning;
using NEDA.Monitoring.MetricsCollector;
using NEDA.KnowledgeBase.DataManagement.Repositories;

namespace NEDA.GameDesign.GameplayDesign.MechanicsDesign;
{
    /// <summary>
    /// Oyun mekanikleri oluşturmak için gelişmiş mekanik yaratıcı motoru;
    /// </summary>
    public class MechanicCreator : IMechanicCreator, IDisposable;
    {
        private readonly ILogger<MechanicCreator> _logger;
        private readonly IConfiguration _configuration;
        private readonly IEventBus _eventBus;
        private readonly IMLModel _mlModel;
        private readonly IMetricsEngine _metricsEngine;
        private readonly IRepository<GameMechanic> _mechanicRepository;
        private readonly IRepository<MechanicTemplate> _templateRepository;

        private readonly SemaphoreSlim _creationLock = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, MechanicCreationSession> _activeSessions;
        private readonly MechanicValidator _validator;

        private MechanicCreationConfig _currentConfig;
        private bool _isInitialized;
        private Timer _cleanupTimer;

        /// <summary>
        /// Mekanik oluşturulduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<MechanicCreatedEventArgs> OnMechanicCreated;

        /// <summary>
        /// Mekanik validasyon başarısız olduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<MechanicValidationFailedEventArgs> OnMechanicValidationFailed;

        /// <summary>
        /// AI önerisi oluşturulduğunda tetiklenen event;
        /// </summary>
        public event EventHandler<AIRecommendationGeneratedEventArgs> OnAIRecommendationGenerated;

        public MechanicCreator(
            ILogger<MechanicCreator> logger,
            IConfiguration configuration,
            IEventBus eventBus,
            IMLModel mlModel,
            IMetricsEngine metricsEngine,
            IRepository<GameMechanic> mechanicRepository,
            IRepository<MechanicTemplate> templateRepository)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _mlModel = mlModel ?? throw new ArgumentNullException(nameof(mlModel));
            _metricsEngine = metricsEngine ?? throw new ArgumentNullException(nameof(metricsEngine));
            _mechanicRepository = mechanicRepository ?? throw new ArgumentNullException(nameof(mechanicRepository));
            _templateRepository = templateRepository ?? throw new ArgumentNullException(nameof(templateRepository));

            _activeSessions = new Dictionary<string, MechanicCreationSession>();
            _validator = new MechanicValidator();
            _isInitialized = false;

            _logger.LogInformation("MechanicCreator initialized");
        }

        /// <summary>
        /// Mekanik yaratıcıyı başlatır;
        /// </summary>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Initializing MechanicCreator...");

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    // Konfigürasyon yükleme;
                    LoadConfiguration();

                    // ML modelini yükle;
                    await LoadMLModelAsync(cancellationToken);

                    // Şablonları yükle;
                    await LoadTemplatesAsync(cancellationToken);

                    // Metrik koleksiyonu başlat;
                    InitializeMetricsCollection();

                    // Temizleme timer'ını başlat;
                    StartCleanupTimer();

                    _isInitialized = true;

                    _logger.LogInformation("MechanicCreator initialized successfully");

                    // Event yayınla;
                    await _eventBus.PublishAsync(new MechanicCreatorInitializedEvent;
                    {
                        Timestamp = DateTime.UtcNow,
                        TemplateCount = (await _templateRepository.GetAllAsync(cancellationToken)).Count(),
                        ActiveModel = _mlModel.ModelName;
                    });
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MechanicCreator");
                throw new MechanicCreatorException("MechanicCreator initialization failed", ex);
            }
        }

        /// <summary>
        /// Yeni bir mekanik oluşturma oturumu başlatır;
        /// </summary>
        public async Task<MechanicCreationSession> StartCreationSessionAsync(
            CreationSessionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Starting creation session for project: {ProjectId}", request.ProjectId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    var sessionId = GenerateSessionId();
                    var session = new MechanicCreationSession;
                    {
                        SessionId = sessionId,
                        ProjectId = request.ProjectId,
                        UserId = request.UserId,
                        StartedAt = DateTime.UtcNow,
                        Status = SessionStatus.Active,
                        Context = new CreationContext;
                        {
                            GameGenre = request.GameGenre,
                            TargetPlatform = request.TargetPlatform,
                            ComplexityLevel = request.ComplexityLevel,
                            DesignGoals = request.DesignGoals ?? new List<string>(),
                            Constraints = request.Constraints ?? new Dictionary<string, object>()
                        },
                        History = new List<CreationStep>(),
                        CreatedMechanics = new List<GameMechanic>(),
                        SessionData = new Dictionary<string, object>()
                    };

                    // AI bağlamını başlat;
                    await InitializeAIContextAsync(session, cancellationToken);

                    // Oturumu kaydet;
                    _activeSessions[sessionId] = session;

                    // Başlangıç önerileri oluştur;
                    var initialSuggestions = await GenerateInitialSuggestionsAsync(session, cancellationToken);
                    session.Suggestions = initialSuggestions;

                    // Metrikleri kaydet;
                    await _metricsEngine.RecordMetricAsync("SessionStarted", new;
                    {
                        SessionId = sessionId,
                        ProjectId = request.ProjectId,
                        UserId = request.UserId,
                        Genre = request.GameGenre,
                        Platform = request.TargetPlatform,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Creation session started: {SessionId}", sessionId);

                    return session;
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start creation session for project: {ProjectId}", request.ProjectId);
                throw new MechanicCreationException("Failed to start creation session", ex);
            }
        }

        /// <summary>
        /// AI destekli mekanik önerisi oluşturur;
        /// </summary>
        public async Task<MechanicSuggestion> GenerateAISuggestionAsync(
            string sessionId,
            SuggestionRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            try
            {
                _logger.LogInformation("Generating AI suggestion for session: {SessionId}", sessionId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    if (!_activeSessions.TryGetValue(sessionId, out var session))
                    {
                        throw new SessionNotFoundException($"Active session not found: {sessionId}");
                    }

                    if (session.Status != SessionStatus.Active)
                    {
                        throw new InvalidSessionStateException($"Session is not active: {session.Status}");
                    }

                    // İsteği geçmişe kaydet;
                    var step = new CreationStep;
                    {
                        StepId = Guid.NewGuid().ToString(),
                        Timestamp = DateTime.UtcNow,
                        ActionType = StepActionType.AISuggestionRequest,
                        RequestData = JsonSerializer.Serialize(request),
                        SessionContext = session.Context;
                    };

                    session.History.Add(step);

                    // AI modelinden öneri al;
                    var aiInput = PrepareAIInput(session, request);
                    var aiOutput = await _mlModel.PredictAsync<AISuggestionInput, AISuggestionOutput>(aiInput, cancellationToken);

                    // AI çıktısını mekanik önerisine dönüştür;
                    var suggestion = ConvertAIOutputToSuggestion(aiOutput, session, request);

                    // Öneriyi validasyondan geçir;
                    var validationResult = await ValidateSuggestionAsync(suggestion, session, cancellationToken);
                    suggestion.ValidationResult = validationResult;

                    // Öneriyi oturuma ekle;
                    if (session.Suggestions == null)
                        session.Suggestions = new List<MechanicSuggestion>();

                    session.Suggestions.Add(suggestion);

                    // Oturum verisini güncelle;
                    UpdateSessionWithSuggestion(session, suggestion);

                    // Metrikleri kaydet;
                    await _metricsEngine.RecordMetricAsync("AISuggestionGenerated", new;
                    {
                        SessionId = sessionId,
                        SuggestionId = suggestion.SuggestionId,
                        MechanicType = suggestion.MechanicType,
                        ConfidenceScore = suggestion.ConfidenceScore,
                        ValidationPassed = validationResult.IsValid,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Event tetikle;
                    OnAIRecommendationGenerated?.Invoke(this, new AIRecommendationGeneratedEventArgs;
                    {
                        SessionId = sessionId,
                        Suggestion = suggestion,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("AI suggestion generated: {SuggestionId}, Confidence: {Confidence}",
                        suggestion.SuggestionId, suggestion.ConfidenceScore);

                    return suggestion;
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate AI suggestion for session: {SessionId}", sessionId);
                throw new AISuggestionException($"Failed to generate AI suggestion for session: {sessionId}", ex);
            }
        }

        /// <summary>
        /// Öneriden mekanik oluşturur;
        /// </summary>
        public async Task<GameMechanic> CreateMechanicFromSuggestionAsync(
            string sessionId,
            string suggestionId,
            MechanicCustomization customization = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (string.IsNullOrWhiteSpace(suggestionId))
                throw new ArgumentException("Suggestion ID cannot be null or empty", nameof(suggestionId));

            try
            {
                _logger.LogInformation("Creating mechanic from suggestion: {SuggestionId} in session: {SessionId}",
                    suggestionId, sessionId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    if (!_activeSessions.TryGetValue(sessionId, out var session))
                    {
                        throw new SessionNotFoundException($"Active session not found: {sessionId}");
                    }

                    // Öneriyi bul;
                    var suggestion = session.Suggestions?.FirstOrDefault(s => s.SuggestionId == suggestionId);
                    if (suggestion == null)
                    {
                        throw new SuggestionNotFoundException($"Suggestion not found: {suggestionId}");
                    }

                    // Validasyon kontrolü;
                    if (!suggestion.ValidationResult.IsValid &&
                        !_currentConfig.AllowCreationFromInvalidSuggestions)
                    {
                        throw new InvalidSuggestionException(
                            $"Cannot create mechanic from invalid suggestion. Validation errors: {string.Join(", ", suggestion.ValidationResult.Errors)}");
                    }

                    // Mekanik oluştur;
                    var mechanic = await BuildMechanicFromSuggestionAsync(suggestion, session, customization, cancellationToken);

                    // Detaylı validasyon;
                    var detailedValidation = await _validator.ValidateMechanicAsync(mechanic, cancellationToken);
                    mechanic.ValidationResult = detailedValidation;

                    if (!detailedValidation.IsValid)
                    {
                        // Validasyon hatası event'i tetikle;
                        OnMechanicValidationFailed?.Invoke(this, new MechanicValidationFailedEventArgs;
                        {
                            SessionId = sessionId,
                            MechanicId = mechanic.Id,
                            ValidationResult = detailedValidation,
                            Timestamp = DateTime.UtcNow;
                        });

                        if (!_currentConfig.AllowInvalidMechanics)
                        {
                            throw new MechanicValidationException(
                                $"Mechanic validation failed: {string.Join(", ", detailedValidation.Errors)}");
                        }
                    }

                    // Mekaniği oturuma ekle;
                    session.CreatedMechanics.Add(mechanic);

                    // Geçmişe kaydet;
                    var step = new CreationStep;
                    {
                        StepId = Guid.NewGuid().ToString(),
                        Timestamp = DateTime.UtcNow,
                        ActionType = StepActionType.MechanicCreated,
                        CreatedMechanicId = mechanic.Id,
                        SessionContext = session.Context;
                    };

                    session.History.Add(step);

                    // Veritabanına kaydet;
                    await _mechanicRepository.AddAsync(mechanic, cancellationToken);
                    await _mechanicRepository.SaveChangesAsync(cancellationToken);

                    // Event tetikle;
                    OnMechanicCreated?.Invoke(this, new MechanicCreatedEventArgs;
                    {
                        SessionId = sessionId,
                        Mechanic = mechanic,
                        SourceSuggestionId = suggestionId,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Event bus'a yayınla;
                    await _eventBus.PublishAsync(new MechanicCreatedEvent;
                    {
                        SessionId = sessionId,
                        MechanicId = mechanic.Id,
                        MechanicType = mechanic.Type,
                        ProjectId = session.ProjectId,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Metrikleri kaydet;
                    await _metricsEngine.RecordMetricAsync("MechanicCreated", new;
                    {
                        SessionId = sessionId,
                        MechanicId = mechanic.Id,
                        MechanicType = mechanic.Type,
                        Complexity = mechanic.Complexity,
                        ValidationPassed = detailedValidation.IsValid,
                        CreationDuration = (DateTime.UtcNow - suggestion.CreatedAt).TotalSeconds,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Mechanic created successfully: {MechanicId}, Type: {Type}",
                        mechanic.Id, mechanic.Type);

                    return mechanic;
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create mechanic from suggestion: {SuggestionId}", suggestionId);
                throw new MechanicCreationException($"Failed to create mechanic from suggestion: {suggestionId}", ex);
            }
        }

        /// <summary>
        /// Şablondan mekanik oluşturur;
        /// </summary>
        public async Task<GameMechanic> CreateMechanicFromTemplateAsync(
            string sessionId,
            string templateId,
            TemplateParameters parameters,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (string.IsNullOrWhiteSpace(templateId))
                throw new ArgumentException("Template ID cannot be null or empty", nameof(templateId));

            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            try
            {
                _logger.LogInformation("Creating mechanic from template: {TemplateId} in session: {SessionId}",
                    templateId, sessionId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    if (!_activeSessions.TryGetValue(sessionId, out var session))
                    {
                        throw new SessionNotFoundException($"Active session not found: {sessionId}");
                    }

                    // Şablonu yükle;
                    var template = await _templateRepository.GetByIdAsync(templateId, cancellationToken);
                    if (template == null)
                    {
                        throw new TemplateNotFoundException($"Template not found: {templateId}");
                    }

                    // Şablonu oturum bağlamına göre uyarla;
                    var adaptedTemplate = await AdaptTemplateToContextAsync(template, session, cancellationToken);

                    // Parametreleri uygula;
                    var mechanic = await ApplyTemplateParametersAsync(adaptedTemplate, parameters, session, cancellationToken);

                    // Mekaniği tamamla;
                    await FinalizeMechanicCreationAsync(mechanic, session, cancellationToken);

                    // Validasyon;
                    var validation = await _validator.ValidateMechanicAsync(mechanic, cancellationToken);
                    mechanic.ValidationResult = validation;

                    if (!validation.IsValid && !_currentConfig.AllowInvalidMechanics)
                    {
                        throw new MechanicValidationException(
                            $"Template-based mechanic validation failed: {string.Join(", ", validation.Errors)}");
                    }

                    // Oturuma ekle ve kaydet;
                    session.CreatedMechanics.Add(mechanic);
                    await _mechanicRepository.AddAsync(mechanic, cancellationToken);
                    await _mechanicRepository.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation("Mechanic created from template: {MechanicId}", mechanic.Id);

                    return mechanic;
                }
                finally
                {
                    _balanceLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create mechanic from template: {TemplateId}", templateId);
                throw new MechanicCreationException($"Failed to create mechanic from template: {templateId}", ex);
            }
        }

        /// <summary>
        /// Mevcut mekaniği klonlar ve modifiye eder;
        /// </summary>
        public async Task<GameMechanic> CloneAndModifyMechanicAsync(
            string sessionId,
            string sourceMechanicId,
            ModificationRequest modification,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (string.IsNullOrWhiteSpace(sourceMechanicId))
                throw new ArgumentException("Source mechanic ID cannot be null or empty", nameof(sourceMechanicId));

            if (modification == null)
                throw new ArgumentNullException(nameof(modification));

            try
            {
                _logger.LogInformation("Cloning and modifying mechanic: {SourceId} in session: {SessionId}",
                    sourceMechanicId, sessionId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    if (!_activeSessions.TryGetValue(sessionId, out var session))
                    {
                        throw new SessionNotFoundException($"Active session not found: {sessionId}");
                    }

                    // Kaynak mekaniği bul;
                    var sourceMechanic = await _mechanicRepository.GetByIdAsync(sourceMechanicId, cancellationToken);
                    if (sourceMechanic == null)
                    {
                        throw new MechanicNotFoundException($"Source mechanic not found: {sourceMechanicId}");
                    }

                    // Klon oluştur;
                    var clonedMechanic = sourceMechanic.Clone();
                    clonedMechanic.Id = Guid.NewGuid().ToString();
                    clonedMechanic.Name = $"{sourceMechanic.Name} (Modified)";
                    clonedMechanic.CreatedAt = DateTime.UtcNow;
                    clonedMechanic.Version = 1;
                    clonedMechanic.ParentMechanicId = sourceMechanicId;

                    // Modifikasyonları uygula;
                    await ApplyModificationsAsync(clonedMechanic, modification, cancellationToken);

                    // Yeni bağlama adapte et;
                    await AdaptToNewContextAsync(clonedMechanic, session, cancellationToken);

                    // Validasyon;
                    var validation = await _validator.ValidateMechanicAsync(clonedMechanic, cancellationToken);
                    clonedMechanic.ValidationResult = validation;

                    if (!validation.IsValid && !_currentConfig.AllowInvalidMechanics)
                    {
                        throw new MechanicValidationException(
                            $"Cloned mechanic validation failed: {string.Join(", ", validation.Errors)}");
                    }

                    // Kaydet;
                    session.CreatedMechanics.Add(clonedMechanic);
                    await _mechanicRepository.AddAsync(clonedMechanic, cancellationToken);
                    await _mechanicRepository.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation("Mechanic cloned and modified: {NewId} from {SourceId}",
                        clonedMechanic.Id, sourceMechanicId);

                    return clonedMechanic;
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to clone and modify mechanic: {SourceId}", sourceMechanicId);
                throw new MechanicCreationException($"Failed to clone and modify mechanic: {sourceMechanicId}", ex);
            }
        }

        /// <summary>
        /// Mekanik kombinasyonu oluşturur;
        /// </summary>
        public async Task<GameMechanic> CombineMechanicsAsync(
            string sessionId,
            CombinationRequest combination,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            if (combination == null)
                throw new ArgumentNullException(nameof(combination));

            if (combination.MechanicIds == null || combination.MechanicIds.Count < 2)
                throw new ArgumentException("At least two mechanics are required for combination", nameof(combination));

            try
            {
                _logger.LogInformation("Combining mechanics in session: {SessionId}", sessionId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    if (!_activeSessions.TryGetValue(sessionId, out var session))
                    {
                        throw new SessionNotFoundException($"Active session not found: {sessionId}");
                    }

                    // Mekanikleri yükle;
                    var mechanics = new List<GameMechanic>();
                    foreach (var mechanicId in combination.MechanicIds)
                    {
                        var mechanic = await _mechanicRepository.GetByIdAsync(mechanicId, cancellationToken);
                        if (mechanic == null)
                        {
                            throw new MechanicNotFoundException($"Mechanic not found: {mechanicId}");
                        }
                        mechanics.Add(mechanic);
                    }

                    // Kombinasyon analizi yap;
                    var analysis = await AnalyzeCombinationPotentialAsync(mechanics, combination, cancellationToken);

                    if (!analysis.IsCombinable && !_currentConfig.AllowUnlikelyCombinations)
                    {
                        throw new CombinationNotPossibleException(
                            $"Mechanics cannot be combined: {analysis.Reason}");
                    }

                    // Kombine mekanik oluştur;
                    var combinedMechanic = await CreateCombinedMechanicAsync(mechanics, analysis, combination, session, cancellationToken);

                    // Validasyon;
                    var validation = await _validator.ValidateMechanicAsync(combinedMechanic, cancellationToken);
                    combinedMechanic.ValidationResult = validation;

                    if (!validation.IsValid && !_currentConfig.AllowInvalidMechanics)
                    {
                        throw new MechanicValidationException(
                            $"Combined mechanic validation failed: {string.Join(", ", validation.Errors)}");
                    }

                    // Kaydet;
                    session.CreatedMechanics.Add(combinedMechanic);
                    await _mechanicRepository.AddAsync(combinedMechanic, cancellationToken);
                    await _mechanicRepository.SaveChangesAsync(cancellationToken);

                    _logger.LogInformation("Mechanics combined successfully: {CombinedId}", combinedMechanic.Id);

                    return combinedMechanic;
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to combine mechanics in session: {SessionId}", sessionId);
                throw new MechanicCreationException("Failed to combine mechanics", ex);
            }
        }

        /// <summary>
        /// Mekanik oluşturma oturumunu sonlandırır;
        /// </summary>
        public async Task<CreationSessionResult> EndCreationSessionAsync(
            string sessionId,
            SessionEndRequest endRequest = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sessionId))
                throw new ArgumentException("Session ID cannot be null or empty", nameof(sessionId));

            try
            {
                _logger.LogInformation("Ending creation session: {SessionId}", sessionId);

                await _creationLock.WaitAsync(cancellationToken);

                try
                {
                    if (!_activeSessions.TryGetValue(sessionId, out var session))
                    {
                        throw new SessionNotFoundException($"Active session not found: {sessionId}");
                    }

                    // Oturum durumunu güncelle;
                    session.Status = SessionStatus.Completed;
                    session.EndedAt = DateTime.UtcNow;
                    session.EndReason = endRequest?.Reason ?? SessionEndReason.Manual;

                    // Oturum verilerini topla;
                    var result = new CreationSessionResult;
                    {
                        SessionId = sessionId,
                        ProjectId = session.ProjectId,
                        UserId = session.UserId,
                        StartTime = session.StartedAt,
                        EndTime = session.EndedAt.Value,
                        Duration = (session.EndedAt.Value - session.StartedAt).TotalSeconds,
                        CreatedMechanicsCount = session.CreatedMechanics.Count,
                        SuggestionsGenerated = session.Suggestions?.Count ?? 0,
                        StepsTaken = session.History.Count,
                        CreatedMechanics = session.CreatedMechanics.ToList(),
                        SessionMetrics = CalculateSessionMetrics(session),
                        SuccessRate = CalculateSuccessRate(session)
                    };

                    // Oturum verilerini temizle (isteğe bağlı)
                    if (_currentConfig.CleanupSessionDataOnEnd)
                    {
                        ClearSessionData(session);
                    }

                    // Aktif oturumlardan kaldır;
                    _activeSessions.Remove(sessionId);

                    // Metrikleri kaydet;
                    await _metricsEngine.RecordMetricAsync("SessionEnded", new;
                    {
                        SessionId = sessionId,
                        Duration = result.Duration,
                        MechanicsCreated = result.CreatedMechanicsCount,
                        SuccessRate = result.SuccessRate,
                        EndReason = session.EndReason.ToString(),
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.LogInformation("Creation session ended: {SessionId}, Created: {Count}",
                        sessionId, result.CreatedMechanicsCount);

                    return result;
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to end creation session: {SessionId}", sessionId);
                throw new MechanicCreationException($"Failed to end creation session: {sessionId}", ex);
            }
        }

        /// <summary>
        /// Mevcut oturumları listeler;
        /// </summary>
        public async Task<IEnumerable<MechanicCreationSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            await _creationLock.WaitAsync(cancellationToken);

            try
            {
                return _activeSessions.Values.ToList();
            }
            finally
            {
                _creationLock.Release();
            }
        }

        /// <summary>
        /// Mekanik şablonlarını getirir;
        /// </summary>
        public async Task<IEnumerable<MechanicTemplate>> GetTemplatesAsync(
            TemplateFilter filter = null,
            CancellationToken cancellationToken = default)
        {
            ValidateInitialized();

            var query = _templateRepository.GetAll();

            if (filter != null)
            {
                if (!string.IsNullOrWhiteSpace(filter.Genre))
                    query = query.Where(t => t.ApplicableGenres.Contains(filter.Genre));

                if (!string.IsNullOrWhiteSpace(filter.MechanicType))
                    query = query.Where(t => t.MechanicType == filter.MechanicType);

                if (filter.ComplexityLevel.HasValue)
                    query = query.Where(t => t.ComplexityLevel == filter.ComplexityLevel.Value);

                if (filter.Platforms != null && filter.Platforms.Any())
                    query = query.Where(t => t.SupportedPlatforms.Any(p => filter.Platforms.Contains(p)));
            }

            return await _templateRepository.ToListAsync(query, cancellationToken);
        }

        #region Private Methods;

        private void LoadConfiguration()
        {
            var configSection = _configuration.GetSection("MechanicCreator");
            _currentConfig = new MechanicCreationConfig;
            {
                MaxActiveSessions = configSection.GetValue<int>("MaxActiveSessions", 50),
                SessionTimeout = configSection.GetValue<TimeSpan>("SessionTimeout", TimeSpan.FromHours(2)),
                CleanupInterval = configSection.GetValue<TimeSpan>("CleanupInterval", TimeSpan.FromMinutes(30)),
                MaxSuggestionsPerSession = configSection.GetValue<int>("MaxSuggestionsPerSession", 100),
                AllowInvalidMechanics = configSection.GetValue<bool>("AllowInvalidMechanics", false),
                AllowCreationFromInvalidSuggestions = configSection.GetValue<bool>("AllowCreationFromInvalidSuggestions", false),
                AllowUnlikelyCombinations = configSection.GetValue<bool>("AllowUnlikelyCombinations", false),
                CleanupSessionDataOnEnd = configSection.GetValue<bool>("CleanupSessionDataOnEnd", true),
                AIConfidenceThreshold = configSection.GetValue<double>("AIConfidenceThreshold", 0.7),
                DefaultComplexity = configSection.GetValue<ComplexityLevel>("DefaultComplexity", ComplexityLevel.Medium)
            };

            _logger.LogDebug("MechanicCreator configuration loaded: {@Configuration}", _currentConfig);
        }

        private async Task LoadMLModelAsync(CancellationToken cancellationToken)
        {
            try
            {
                // ML modelini yükle veya eğit;
                if (!_mlModel.IsLoaded)
                {
                    _logger.LogInformation("Loading ML model for mechanic creation...");
                    await _mlModel.LoadAsync(cancellationToken);
                }

                _logger.LogInformation("ML model loaded: {ModelName}, Version: {Version}",
                    _mlModel.ModelName, _mlModel.ModelVersion);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load ML model");
                throw new MLModelException("Failed to load ML model for mechanic creation", ex);
            }
        }

        private async Task LoadTemplatesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Varsayılan şablonları yükle (eğer veritabanı boşsa)
                var templateCount = await _templateRepository.CountAsync(cancellationToken);

                if (templateCount == 0)
                {
                    _logger.LogInformation("Loading default mechanic templates...");
                    await LoadDefaultTemplatesAsync(cancellationToken);
                }

                _logger.LogInformation("Loaded {Count} mechanic templates", templateCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load templates");
                throw new TemplateLoadingException("Failed to load mechanic templates", ex);
            }
        }

        private async Task LoadDefaultTemplatesAsync(CancellationToken cancellationToken)
        {
            var defaultTemplates = new List<MechanicTemplate>
            {
                // Movement Templates;
                new MechanicTemplate;
                {
                    Id = "move_basic",
                    Name = "Basic Movement",
                    Description = "Standard character movement with walking and jumping",
                    MechanicType = MechanicType.Movement,
                    ComplexityLevel = ComplexityLevel.Low,
                    TemplateData = new TemplateData;
                    {
                        Parameters = new Dictionary<string, TemplateParameter>
                        {
                            ["speed"] = new TemplateParameter { DefaultValue = 5.0, Min = 1.0, Max = 20.0 },
                            ["jump_force"] = new TemplateParameter { DefaultValue = 10.0, Min = 5.0, Max = 30.0 },
                            ["gravity"] = new TemplateParameter { DefaultValue = 9.81, Min = 1.0, Max = 20.0 }
                        },
                        Implementation = "BasicMovementSystem",
                        Dependencies = new List<string> { "PhysicsSystem", "InputSystem" }
                    },
                    ApplicableGenres = new List<string> { "Platformer", "Action", "Adventure" },
                    SupportedPlatforms = new List<string> { "PC", "Console", "Mobile" },
                    Tags = new List<string> { "movement", "basic", "character" }
                },
                
                // Combat Templates;
                new MechanicTemplate;
                {
                    Id = "combat_basic",
                    Name = "Basic Combat",
                    Description = "Simple attack and damage system",
                    MechanicType = MechanicType.Combat,
                    ComplexityLevel = ComplexityLevel.Medium,
                    TemplateData = new TemplateData;
                    {
                        Parameters = new Dictionary<string, TemplateParameter>
                        {
                            ["damage"] = new TemplateParameter { DefaultValue = 10, Min = 1, Max = 100 },
                            ["attack_speed"] = new TemplateParameter { DefaultValue = 1.0, Min = 0.1, Max = 5.0 },
                            ["range"] = new TemplateParameter { DefaultValue = 2.0, Min = 0.5, Max = 10.0 }
                        },
                        Implementation = "BasicCombatSystem",
                        Dependencies = new List<string> { "AnimationSystem", "DamageSystem" }
                    },
                    ApplicableGenres = new List<string> { "Action", "RPG", "Adventure" },
                    SupportedPlatforms = new List<string> { "PC", "Console" },
                    Tags = new List<string> { "combat", "attack", "damage" }
                },
                
                // Economy Templates;
                new MechanicTemplate;
                {
                    Id = "economy_basic",
                    Name = "Basic Economy",
                    Description = "Simple currency and trading system",
                    MechanicType = MechanicType.Economy,
                    ComplexityLevel = ComplexityLevel.Medium,
                    TemplateData = new TemplateData;
                    {
                        Parameters = new Dictionary<string, TemplateParameter>
                        {
                            ["currency_name"] = new TemplateParameter { DefaultValue = "Gold", IsString = true },
                            ["starting_amount"] = new TemplateParameter { DefaultValue = 100, Min = 0, Max = 10000 },
                            ["inflation_rate"] = new TemplateParameter { DefaultValue = 0.1, Min = 0.0, Max = 1.0 }
                        },
                        Implementation = "BasicEconomySystem",
                        Dependencies = new List<string> { "InventorySystem", "UI System" }
                    },
                    ApplicableGenres = new List<string> { "RPG", "Simulation", "Strategy" },
                    SupportedPlatforms = new List<string> { "PC", "Console", "Mobile" },
                    Tags = new List<string> { "economy", "currency", "trading" }
                }
            };

            foreach (var template in defaultTemplates)
            {
                await _templateRepository.AddAsync(template, cancellationToken);
            }

            await _templateRepository.SaveChangesAsync(cancellationToken);
        }

        private void InitializeMetricsCollection()
        {
            _metricsEngine.RegisterMetric("mechanic_created", "Number of mechanics created", MetricType.Counter);
            _metricsEngine.RegisterMetric("session_duration", "Duration of creation sessions", MetricType.Histogram);
            _metricsEngine.RegisterMetric("ai_suggestions", "Number of AI suggestions generated", MetricType.Counter);
            _metricsEngine.RegisterMetric("validation_errors", "Number of validation errors", MetricType.Counter);

            _logger.LogDebug("MechanicCreator metrics collection initialized");
        }

        private void StartCleanupTimer()
        {
            _cleanupTimer = new Timer(
                async _ => await CleanupStaleSessionsAsync(),
                null,
                _currentConfig.CleanupInterval,
                _currentConfig.CleanupInterval);

            _logger.LogInformation("Cleanup timer started with interval: {Interval}", _currentConfig.CleanupInterval);
        }

        private async Task CleanupStaleSessionsAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                await _creationLock.WaitAsync();

                try
                {
                    var staleSessions = _activeSessions;
                        .Where(kvp =>
                            (DateTime.UtcNow - kvp.Value.StartedAt) > _currentConfig.SessionTimeout ||
                            kvp.Value.Status != SessionStatus.Active)
                        .ToList();

                    foreach (var staleSession in staleSessions)
                    {
                        _logger.LogInformation("Cleaning up stale session: {SessionId}", staleSession.Key);

                        // Oturumu sonlandır;
                        staleSession.Value.Status = SessionStatus.Timeout;
                        staleSession.Value.EndedAt = DateTime.UtcNow;
                        staleSession.Value.EndReason = SessionEndReason.Timeout;

                        // Oturum verilerini temizle;
                        ClearSessionData(staleSession.Value);

                        // Aktif oturumlardan kaldır;
                        _activeSessions.Remove(staleSession.Key);

                        // Metrikleri kaydet;
                        await _metricsEngine.RecordMetricAsync("SessionTimeout", new;
                        {
                            SessionId = staleSession.Key,
                            Duration = (DateTime.UtcNow - staleSession.Value.StartedAt).TotalSeconds,
                            MechanicsCreated = staleSession.Value.CreatedMechanics.Count,
                            Timestamp = DateTime.UtcNow;
                        });
                    }

                    if (staleSessions.Count > 0)
                    {
                        _logger.LogInformation("Cleaned up {Count} stale sessions", staleSessions.Count);
                    }
                }
                finally
                {
                    _creationLock.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup stale sessions");
            }
        }

        private string GenerateSessionId()
        {
            return $"MECH_SESS_{Guid.NewGuid():N}_{DateTime.UtcNow:yyyyMMddHHmmss}";
        }

        private async Task InitializeAIContextAsync(MechanicCreationSession session, CancellationToken cancellationToken)
        {
            // AI modeli için bağlam verilerini hazırla;
            session.SessionData["ai_context"] = new AIContext;
            {
                SessionId = session.SessionId,
                ProjectId = session.ProjectId,
                GameGenre = session.Context.GameGenre,
                TargetPlatform = session.Context.TargetPlatform,
                DesignGoals = session.Context.DesignGoals,
                Constraints = session.Context.Constraints,
                CreationTimestamp = session.StartedAt;
            };

            // Benzer projelerden mekanikleri yükle (öğrenme için)
            var similarMechanics = await FindSimilarMechanicsAsync(session.Context, cancellationToken);
            session.SessionData["similar_mechanics"] = similarMechanics;

            _logger.LogDebug("AI context initialized for session: {SessionId}", session.SessionId);
        }

        private async Task<IEnumerable<MechanicSuggestion>> GenerateInitialSuggestionsAsync(
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            var suggestions = new List<MechanicSuggestion>();

            // Bağlama göre başlangıç önerileri oluştur;
            var context = session.Context;

            // Genre-specific öneriler;
            var genreSuggestions = await GenerateGenreBasedSuggestionsAsync(context.GameGenre, cancellationToken);
            suggestions.AddRange(genreSuggestions);

            // Platform-specific öneriler;
            var platformSuggestions = await GeneratePlatformBasedSuggestionsAsync(context.TargetPlatform, cancellationToken);
            suggestions.AddRange(platformSuggestions);

            // Complexity-based öneriler;
            var complexitySuggestions = GenerateComplexityBasedSuggestions(context.ComplexityLevel);
            suggestions.AddRange(complexitySuggestions);

            // Her öneriyi oturum bağlamına adapte et;
            foreach (var suggestion in suggestions)
            {
                AdaptSuggestionToContext(suggestion, session);
            }

            return suggestions.Take(_currentConfig.MaxSuggestionsPerSession / 10).ToList(); // İlk %10;
        }

        private AISuggestionInput PrepareAIInput(MechanicCreationSession session, SuggestionRequest request)
        {
            return new AISuggestionInput;
            {
                SessionContext = session.Context,
                RequestType = request.RequestType,
                FocusArea = request.FocusArea,
                ReferenceMechanics = request.ReferenceMechanics,
                Constraints = request.AdditionalConstraints ?? new Dictionary<string, object>(),
                SessionHistory = session.History.TakeLast(10).ToList(), // Son 10 adım;
                CreatedMechanics = session.CreatedMechanics,
                SimilarMechanics = session.SessionData.ContainsKey("similar_mechanics")
                    ? (IEnumerable<GameMechanic>)session.SessionData["similar_mechanics"]
                    : new List<GameMechanic>(),
                Timestamp = DateTime.UtcNow;
            };
        }

        private MechanicSuggestion ConvertAIOutputToSuggestion(
            AISuggestionOutput aiOutput,
            MechanicCreationSession session,
            SuggestionRequest request)
        {
            var suggestion = new MechanicSuggestion;
            {
                SuggestionId = Guid.NewGuid().ToString(),
                SessionId = session.SessionId,
                GeneratedAt = DateTime.UtcNow,
                MechanicType = aiOutput.MechanicType,
                Name = aiOutput.Name,
                Description = aiOutput.Description,
                CoreConcept = aiOutput.CoreConcept,
                KeyFeatures = aiOutput.KeyFeatures,
                Complexity = aiOutput.Complexity,
                ConfidenceScore = aiOutput.ConfidenceScore,
                ImplementationNotes = aiOutput.ImplementationNotes,
                EstimatedDevelopmentTime = aiOutput.EstimatedDevelopmentTime,
                Dependencies = aiOutput.Dependencies,
                Tags = aiOutput.Tags,
                AIReasoning = aiOutput.Reasoning,
                SourceRequest = request;
            };

            // Parametreleri ekle;
            if (aiOutput.Parameters != null)
            {
                suggestion.Parameters = aiOutput.Parameters.Select(p => new MechanicParameter;
                {
                    Name = p.Key,
                    Value = p.Value,
                    DataType = InferDataType(p.Value)
                }).ToList();
            }

            return suggestion;
        }

        private async Task<ValidationResult> ValidateSuggestionAsync(
            MechanicSuggestion suggestion,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            var validation = new ValidationResult();

            // Temel validasyonlar;
            if (string.IsNullOrWhiteSpace(suggestion.Name))
                validation.Errors.Add("Suggestion name cannot be empty");

            if (string.IsNullOrWhiteSpace(suggestion.Description))
                validation.Errors.Add("Suggestion description cannot be empty");

            if (suggestion.ConfidenceScore < _currentConfig.AIConfidenceThreshold)
                validation.Warnings.Add($"Low confidence score: {suggestion.ConfidenceScore}");

            // Bağlam validasyonu;
            if (!IsMechanicTypeAppropriateForGenre(suggestion.MechanicType, session.Context.GameGenre))
                validation.Warnings.Add($"Mechanic type {suggestion.MechanicType} may not be appropriate for genre {session.Context.GameGenre}");

            if (!IsMechanicSuitableForPlatform(suggestion, session.Context.TargetPlatform))
                validation.Warnings.Add($"Mechanic may not be suitable for platform {session.Context.TargetPlatform}");

            // Karmaşıklık kontrolü;
            if (suggestion.Complexity > session.Context.ComplexityLevel)
                validation.Warnings.Add($"Mechanic complexity ({suggestion.Complexity}) exceeds requested level ({session.Context.ComplexityLevel})");

            // Dependency kontrolü;
            var missingDeps = await CheckMissingDependenciesAsync(suggestion.Dependencies, cancellationToken);
            if (missingDeps.Any())
                validation.Warnings.Add($"Missing dependencies: {string.Join(", ", missingDeps)}");

            // Benzer mekanik kontrolü;
            var similarExisting = await FindSimilarExistingMechanicsAsync(suggestion, cancellationToken);
            if (similarExisting.Any())
                validation.Info.Add($"Similar mechanics exist: {string.Join(", ", similarExisting.Select(m => m.Name))}");

            validation.IsValid = !validation.Errors.Any();

            return validation;
        }

        private void UpdateSessionWithSuggestion(MechanicCreationSession session, MechanicSuggestion suggestion)
        {
            // Oturum verilerini güncelle;
            if (!session.SessionData.ContainsKey("ai_suggestions"))
                session.SessionData["ai_suggestions"] = new List<MechanicSuggestion>();

            ((List<MechanicSuggestion>)session.SessionData["ai_suggestions"]).Add(suggestion);

            // AI öğrenme verilerini güncelle;
            UpdateAILearningData(session, suggestion);
        }

        private async Task<GameMechanic> BuildMechanicFromSuggestionAsync(
            MechanicSuggestion suggestion,
            MechanicCreationSession session,
            MechanicCustomization customization,
            CancellationToken cancellationToken)
        {
            var mechanic = new GameMechanic;
            {
                Id = Guid.NewGuid().ToString(),
                Name = customization?.Name ?? suggestion.Name,
                Description = customization?.Description ?? suggestion.Description,
                Type = suggestion.MechanicType,
                Complexity = suggestion.Complexity,
                Status = MechanicStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = session.UserId,
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                SourceSuggestionId = suggestion.SuggestionId,
                Version = 1,

                // Core özellikler;
                CoreMechanic = suggestion.CoreConcept,
                KeyFeatures = suggestion.KeyFeatures?.ToList() ?? new List<string>(),
                ImplementationNotes = suggestion.ImplementationNotes,

                // Parametreler;
                Parameters = suggestion.Parameters?.Select(p => new MechanicParameter;
                {
                    Name = p.Name,
                    Value = customization?.ParameterOverrides?.ContainsKey(p.Name) == true;
                        ? customization.ParameterOverrides[p.Name]
                        : p.Value,
                    DataType = p.DataType,
                    IsConfigurable = true;
                }).ToList() ?? new List<MechanicParameter>(),

                // Metadata;
                Tags = suggestion.Tags?.Concat(customization?.AdditionalTags ?? new List<string>()).ToList()
                    ?? new List<string>(),
                Dependencies = suggestion.Dependencies?.ToList() ?? new List<string>(),
                EstimatedDevTime = suggestion.EstimatedDevelopmentTime,

                // AI bilgileri;
                AIGenerated = true,
                AIConfidence = suggestion.ConfidenceScore,
                AIReasoning = suggestion.AIReasoning;
            };

            // Customizasyonları uygula;
            if (customization != null)
            {
                ApplyCustomization(mechanic, customization);
            }

            // Bağlama adapte et;
            await AdaptMechanicToContextAsync(mechanic, session, cancellationToken);

            // Implementation detaylarını oluştur;
            await GenerateImplementationDetailsAsync(mechanic, cancellationToken);

            return mechanic;
        }

        private void ApplyCustomization(GameMechanic mechanic, MechanicCustomization customization)
        {
            if (customization == null) return;

            if (!string.IsNullOrWhiteSpace(customization.Name))
                mechanic.Name = customization.Name;

            if (!string.IsNullOrWhiteSpace(customization.Description))
                mechanic.Description = customization.Description;

            if (customization.Complexity.HasValue)
                mechanic.Complexity = customization.Complexity.Value;

            if (customization.AdditionalFeatures != null)
                mechanic.KeyFeatures.AddRange(customization.AdditionalFeatures);

            if (customization.ParameterOverrides != null)
            {
                foreach (var paramOverride in customization.ParameterOverrides)
                {
                    var existingParam = mechanic.Parameters?.FirstOrDefault(p => p.Name == paramOverride.Key);
                    if (existingParam != null)
                    {
                        existingParam.Value = paramOverride.Value;
                    }
                    else;
                    {
                        mechanic.Parameters.Add(new MechanicParameter;
                        {
                            Name = paramOverride.Key,
                            Value = paramOverride.Value,
                            DataType = InferDataType(paramOverride.Value),
                            IsConfigurable = true;
                        });
                    }
                }
            }
        }

        private async Task AdaptMechanicToContextAsync(
            GameMechanic mechanic,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            // Platform adaptasyonu;
            await AdaptForPlatformAsync(mechanic, session.Context.TargetPlatform, cancellationToken);

            // Genre adaptasyonu;
            await AdaptForGenreAsync(mechanic, session.Context.GameGenre, cancellationToken);

            // Complexity adaptasyonu;
            AdjustForComplexity(mechanic, session.Context.ComplexityLevel);

            // Design goals adaptasyonu;
            if (session.Context.DesignGoals.Any())
            {
                mechanic.DesignGoalAlignment = CalculateGoalAlignment(mechanic, session.Context.DesignGoals);
            }

            // Constraint adaptasyonu;
            if (session.Context.Constraints.Any())
            {
                ApplyConstraints(mechanic, session.Context.Constraints);
            }
        }

        private async Task GenerateImplementationDetailsAsync(GameMechanic mechanic, CancellationToken cancellationToken)
        {
            // Implementation blueprint oluştur;
            mechanic.ImplementationBlueprint = new ImplementationBlueprint;
            {
                SystemArchitecture = GenerateSystemArchitecture(mechanic),
                CodeStructure = GenerateCodeStructure(mechanic),
                RequiredComponents = GenerateRequiredComponents(mechanic),
                IntegrationPoints = GenerateIntegrationPoints(mechanic),
                TestingStrategy = GenerateTestingStrategy(mechanic)
            };

            // Asset gereksinimlerini belirle;
            mechanic.AssetRequirements = await GenerateAssetRequirementsAsync(mechanic, cancellationToken);

            // Performance considerations;
            mechanic.PerformanceConsiderations = GeneratePerformanceConsiderations(mechanic);
        }

        private async Task<MechanicTemplate> AdaptTemplateToContextAsync(
            MechanicTemplate template,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            var adapted = template.Clone();

            // Genre adaptasyonu;
            if (!adapted.ApplicableGenres.Contains(session.Context.GameGenre))
            {
                adapted.ApplicableGenres.Add(session.Context.GameGenre);
            }

            // Platform adaptasyonu;
            if (!adapted.SupportedPlatforms.Contains(session.Context.TargetPlatform))
            {
                adapted.SupportedPlatforms.Add(session.Context.TargetPlatform);
            }

            // Complexity adaptasyonu;
            if (session.Context.ComplexityLevel != adapted.ComplexityLevel)
            {
                adapted.ComplexityLevel = session.Context.ComplexityLevel;
                await AdjustTemplateComplexityAsync(adapted, session.Context.ComplexityLevel, cancellationToken);
            }

            return adapted;
        }

        private async Task<GameMechanic> ApplyTemplateParametersAsync(
            MechanicTemplate template,
            TemplateParameters parameters,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            var mechanic = new GameMechanic;
            {
                Id = Guid.NewGuid().ToString(),
                Name = parameters.Name ?? template.Name,
                Description = parameters.Description ?? template.Description,
                Type = template.MechanicType,
                Complexity = template.ComplexityLevel,
                Status = MechanicStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = session.UserId,
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                SourceTemplateId = template.Id,
                Version = 1,
                TemplateBased = true;
            };

            // Template parametrelerini uygula;
            if (template.TemplateData?.Parameters != null)
            {
                mechanic.Parameters = new List<MechanicParameter>();

                foreach (var templateParam in template.TemplateData.Parameters)
                {
                    object value;

                    if (parameters.ParameterValues != null &&
                        parameters.ParameterValues.ContainsKey(templateParam.Key))
                    {
                        value = parameters.ParameterValues[templateParam.Key];
                    }
                    else;
                    {
                        value = templateParam.Value.DefaultValue;
                    }

                    mechanic.Parameters.Add(new MechanicParameter;
                    {
                        Name = templateParam.Key,
                        Value = value,
                        DataType = templateParam.Value.IsString ? typeof(string) : InferDataType(value),
                        IsConfigurable = true,
                        MinValue = templateParam.Value.Min,
                        MaxValue = templateParam.Value.Max;
                    });
                }
            }

            // Implementation detayları;
            if (template.TemplateData != null)
            {
                mechanic.Dependencies = template.TemplateData.Dependencies?.ToList() ?? new List<string>();
                mechanic.ImplementationNotes = $"Based on template: {template.Name}";
            }

            // Tag'leri birleştir;
            mechanic.Tags = template.Tags?.ToList() ?? new List<string>();
            if (parameters.AdditionalTags != null)
                mechanic.Tags.AddRange(parameters.AdditionalTags);

            return mechanic;
        }

        private async Task FinalizeMechanicCreationAsync(
            GameMechanic mechanic,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            // Bağlama adapte et;
            await AdaptMechanicToContextAsync(mechanic, session, cancellationToken);

            // Implementation detaylarını oluştur;
            await GenerateImplementationDetailsAsync(mechanic, cancellationToken);

            // Metadata ekle;
            mechanic.CreatedAt = DateTime.UtcNow;
            mechanic.UpdatedAt = DateTime.UtcNow;
            mechanic.CreatedBy = session.UserId;
        }

        private async Task ApplyModificationsAsync(
            GameMechanic mechanic,
            ModificationRequest modification,
            CancellationToken cancellationToken)
        {
            if (modification.NameChange != null)
            {
                mechanic.Name = modification.NameChange.NewName;
                if (!string.IsNullOrWhiteSpace(modification.NameChange.Reason))
                {
                    mechanic.ModificationHistory.Add(new ModificationRecord;
                    {
                        Timestamp = DateTime.UtcNow,
                        ChangeType = "NameChange",
                        Description = modification.NameChange.Reason,
                        OldValue = modification.NameChange.OldName,
                        NewValue = modification.NameChange.NewName;
                    });
                }
            }

            if (modification.ParameterChanges != null)
            {
                foreach (var paramChange in modification.ParameterChanges)
                {
                    var param = mechanic.Parameters?.FirstOrDefault(p => p.Name == paramChange.ParameterName);
                    if (param != null)
                    {
                        var oldValue = param.Value;
                        param.Value = paramChange.NewValue;

                        mechanic.ModificationHistory.Add(new ModificationRecord;
                        {
                            Timestamp = DateTime.UtcNow,
                            ChangeType = "ParameterChange",
                            Description = paramChange.Reason,
                            ParameterName = paramChange.ParameterName,
                            OldValue = oldValue,
                            NewValue = paramChange.NewValue;
                        });
                    }
                }
            }

            if (modification.FeatureAdditions != null)
            {
                foreach (var feature in modification.FeatureAdditions)
                {
                    mechanic.KeyFeatures.Add(feature);

                    mechanic.ModificationHistory.Add(new ModificationRecord;
                    {
                        Timestamp = DateTime.UtcNow,
                        ChangeType = "FeatureAddition",
                        Description = $"Added feature: {feature}",
                        NewValue = feature;
                    });
                }
            }

            if (modification.FeatureRemovals != null)
            {
                foreach (var feature in modification.FeatureRemovals)
                {
                    mechanic.KeyFeatures.Remove(feature);

                    mechanic.ModificationHistory.Add(new ModificationRecord;
                    {
                        Timestamp = DateTime.UtcNow,
                        ChangeType = "FeatureRemoval",
                        Description = $"Removed feature: {feature}",
                        OldValue = feature;
                    });
                }
            }

            // Versiyonu artır;
            mechanic.Version++;
            mechanic.UpdatedAt = DateTime.UtcNow;

            await Task.CompletedTask;
        }

        private async Task AdaptToNewContextAsync(
            GameMechanic mechanic,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            // Yeni bağlama adapte et;
            await AdaptMechanicToContextAsync(mechanic, session, cancellationToken);

            // Modification history'yi temizle (isteğe bağlı)
            if (!_currentConfig.KeepModificationHistoryOnClone)
            {
                mechanic.ModificationHistory.Clear();
            }
        }

        private async Task<CombinationAnalysis> AnalyzeCombinationPotentialAsync(
            List<GameMechanic> mechanics,
            CombinationRequest combination,
            CancellationToken cancellationToken)
        {
            var analysis = new CombinationAnalysis;
            {
                MechanicsCount = mechanics.Count,
                UniqueTypes = mechanics.Select(m => m.Type).Distinct().ToList(),
                IsCombinable = true,
                CompatibilityScore = 1.0,
                PotentialIssues = new List<string>(),
                SynergyOpportunities = new List<string>()
            };

            // Uyumluluk kontrolü;
            foreach (var mech1 in mechanics)
            {
                foreach (var mech2 in mechanics)
                {
                    if (mech1.Id == mech2.Id) continue;

                    var compatibility = await CheckMechanicCompatibilityAsync(mech1, mech2, cancellationToken);
                    if (!compatibility.IsCompatible)
                    {
                        analysis.IsCombinable = false;
                        analysis.PotentialIssues.Add($"Incompatibility between {mech1.Name} and {mech2.Name}: {compatibility.Reason}");
                        analysis.CompatibilityScore *= 0.5; // Uyumsuzluk durumunda skoru düşür;
                    }
                    else;
                    {
                        if (compatibility.SynergyPotential > 0.7)
                        {
                            analysis.SynergyOpportunities.Add($"Strong synergy between {mech1.Name} and {mech2.Name}");
                            analysis.CompatibilityScore *= 1.2; // Sinerji durumunda skoru artır;
                        }
                    }
                }
            }

            // Complexity analizi;
            var avgComplexity = mechanics.Average(m => (int)m.Complexity);
            analysis.ResultingComplexity = (ComplexityLevel)Math.Min((int)ComplexityLevel.VeryHigh, (int)Math.Ceiling(avgComplexity * 1.2));

            if (analysis.ResultingComplexity > combination.MaxAllowedComplexity)
            {
                analysis.Warnings.Add($"Resulting complexity ({analysis.ResultingComplexity}) exceeds maximum allowed ({combination.MaxAllowedComplexity})");
            }

            // Dependency analizi;
            var allDependencies = mechanics.SelectMany(m => m.Dependencies ?? new List<string>()).Distinct().ToList();
            analysis.CombinedDependencies = allDependencies;

            // Resource gereksinimleri;
            analysis.ResourceRequirements = await EstimateCombinedResourceRequirementsAsync(mechanics, cancellationToken);

            // Sonuç;
            if (!analysis.IsCombinable)
            {
                analysis.Reason = "Mechanics are incompatible for combination";
            }
            else if (analysis.CompatibilityScore < 0.5)
            {
                analysis.IsCombinable = false;
                analysis.Reason = "Low compatibility score";
            }

            return analysis;
        }

        private async Task<GameMechanic> CreateCombinedMechanicAsync(
            List<GameMechanic> mechanics,
            CombinationAnalysis analysis,
            CombinationRequest combination,
            MechanicCreationSession session,
            CancellationToken cancellationToken)
        {
            var combined = new GameMechanic;
            {
                Id = Guid.NewGuid().ToString(),
                Name = combination.Name ?? $"Combined_{string.Join("_", mechanics.Select(m => m.Name))}",
                Description = $"Combination of: {string.Join(", ", mechanics.Select(m => m.Name))}",
                Type = DetermineCombinedType(mechanics),
                Complexity = analysis.ResultingComplexity,
                Status = MechanicStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = session.UserId,
                ProjectId = session.ProjectId,
                SessionId = session.SessionId,
                Version = 1,
                IsCombination = true,
                SourceMechanicIds = mechanics.Select(m => m.Id).ToList()
            };

            // Core concept birleştirme;
            combined.CoreMechanic = CombineCoreConcepts(mechanics);

            // Key features birleştirme;
            combined.KeyFeatures = mechanics;
                .SelectMany(m => m.KeyFeatures)
                .Distinct()
                .ToList();

            // Parameters birleştirme;
            combined.Parameters = await CombineParametersAsync(mechanics, cancellationToken);

            // Dependencies birleştirme;
            combined.Dependencies = analysis.CombinedDependencies;

            // Implementation notes birleştirme;
            combined.ImplementationNotes = $"Combined mechanic created from {mechanics.Count} source mechanics.\n" +
                                          $"Compatibility score: {analysis.CompatibilityScore:F2}\n" +
                                          $"Synergies: {string.Join(", ", analysis.SynergyOpportunities)}";

            // Tags birleştirme;
            combined.Tags = mechanics;
                .SelectMany(m => m.Tags ?? new List<string>())
                .Append("combined")
                .Distinct()
                .ToList();

            // Combination metadata;
            combined.CombinationMetadata = new CombinationMetadata;
            {
                SourceMechanics = mechanics.Select(m => new SourceMechanicInfo;
                {
                    Id = m.Id,
                    Name = m.Name,
                    Type = m.Type,
                    Complexity = m.Complexity;
                }).ToList(),
                Analysis = analysis,
                CombinationStrategy = combination.Strategy,
                CreatedAt = DateTime.UtcNow;
            };

            return combined;
        }

        private SessionMetrics CalculateSessionMetrics(MechanicCreationSession session)
        {
            return new SessionMetrics;
            {
                TotalTime = (session.EndedAt.Value - session.StartedAt).TotalSeconds,
                ActiveTime = CalculateActiveTime(session),
                SuggestionsGenerated = session.Suggestions?.Count ?? 0,
                MechanicsCreated = session.CreatedMechanics.Count,
                AverageCreationTime = session.CreatedMechanics.Any()
                    ? session.CreatedMechanics.Average(m => (m.UpdatedAt - m.CreatedAt).TotalSeconds)
                    : 0,
                ValidationPassRate = CalculateValidationPassRate(session),
                AISuggestionAcceptanceRate = CalculateAIAcceptanceRate(session)
            };
        }

        private double CalculateSuccessRate(MechanicCreationSession session)
        {
            if (session.CreatedMechanics.Count == 0)
                return 0;

            var validMechanics = session.CreatedMechanics.Count(m =>
                m.ValidationResult?.IsValid != false);

            return (double)validMechanics / session.CreatedMechanics.Count;
        }

        private void ClearSessionData(MechanicCreationSession session)
        {
            // Büyük verileri temizle, metadata'yı koru;
            session.Suggestions = null;
            session.SessionData.Clear();
            session.History.Clear();

            // Sadece mekanik referanslarını koru, detayları temizle;
            foreach (var mechanic in session.CreatedMechanics)
            {
                mechanic.ImplementationBlueprint = null;
                mechanic.AssetRequirements = null;
                mechanic.PerformanceConsiderations = null;
            }
        }

        #region Helper Methods;

        private async Task<IEnumerable<GameMechanic>> FindSimilarMechanicsAsync(CreationContext context, CancellationToken cancellationToken)
        {
            // Benzer genre ve platformdaki mekanikleri bul;
            var query = _mechanicRepository.GetAll()
                .Where(m => m.Tags.Contains(context.GameGenre) ||
                           m.ProjectId.Contains(context.GameGenre)) // Basit eşleştirme;
                .Where(m => m.Status == MechanicStatus.Completed || m.Status == MechanicStatus.Validated)
                .OrderByDescending(m => m.CreatedAt)
                .Take(50);

            return await _templateRepository.ToListAsync(query, cancellationToken);
        }

        private async Task<IEnumerable<MechanicSuggestion>> GenerateGenreBasedSuggestionsAsync(
            string genre,
            CancellationToken cancellationToken)
        {
            var suggestions = new List<MechanicSuggestion>();

            // Genre'a özel öneri şablonları;
            var genreTemplates = await _templateRepository.GetAll()
                .Where(t => t.ApplicableGenres.Contains(genre))
                .Take(5)
                .ToListAsync(cancellationToken);

            foreach (var template in genreTemplates)
            {
                suggestions.Add(new MechanicSuggestion;
                {
                    SuggestionId = Guid.NewGuid().ToString(),
                    GeneratedAt = DateTime.UtcNow,
                    MechanicType = template.MechanicType,
                    Name = $"{genre} {template.Name}",
                    Description = template.Description,
                    Complexity = template.ComplexityLevel,
                    ConfidenceScore = 0.8,
                    Source = "GenreTemplate",
                    Tags = template.Tags.ToList()
                });
            }

            return suggestions;
        }

        private async Task<IEnumerable<MechanicSuggestion>> GeneratePlatformBasedSuggestionsAsync(
            string platform,
            CancellationToken cancellationToken)
        {
            var suggestions = new List<MechanicSuggestion>();

            // Platform'a özel öneriler;
            var platformSpecificMechanics = new Dictionary<string, List<string>>
            {
                ["Mobile"] = new List<string> { "Touch Controls", "Swipe Gestures", "Tap Mechanics", "Tilt Controls", "Virtual Joystick" },
                ["PC"] = new List<string> { "Keyboard Shortcuts", "Mouse Precision", "Hotkey Systems", "Mod Support", "Custom Keybindings" },
                ["Console"] = new List<string> { "Controller Vibration", "Button Combos", "Trigger Sensitivity", "Quick Time Events", "Aim Assist" },
                ["VR"] = new List<string> { "Hand Tracking", "Room Scale", "Teleport Movement", "Grab Mechanics", "Gesture Recognition" }
            };

            if (platformSpecificMechanics.TryGetValue(platform, out var mechanicNames))
            {
                foreach (var name in mechanicNames.Take(3))
                {
                    suggestions.Add(new MechanicSuggestion;
                    {
                        SuggestionId = Guid.NewGuid().ToString(),
                        GeneratedAt = DateTime.UtcNow,
                        MechanicType = MechanicType.Control,
                        Name = name,
                        Description = $"{platform}-specific {name.ToLower()} mechanic",
                        Complexity = ComplexityLevel.Medium,
                        ConfidenceScore = 0.85,
                        Source = "PlatformTemplate",
                        Tags = new List<string> { platform.ToLower(), "controls" }
                    });
                }
            }

            return suggestions;
        }

        private IEnumerable<MechanicSuggestion> GenerateComplexityBasedSuggestions(ComplexityLevel complexity)
        {
            var suggestions = new List<MechanicSuggestion>();

            // Complexity seviyesine göre öneriler;
            var complexityMechanics = new Dictionary<ComplexityLevel, List<(string, string)>>
            {
                [ComplexityLevel.Low] = new List<(string, string)>
                {
                    ("Simple Scoring", "Basic point collection system"),
                    ("Timer", "Countdown or stopwatch mechanic"),
                    ("Collectibles", "Simple item collection")
                },
                [ComplexityLevel.Medium] = new List<(string, string)>
                {
                    ("Combo System", "Chained action reward system"),
                    ("Resource Management", "Limited resource allocation"),
                    ("Skill Tree", "Progressive ability unlocking")
                },
                [ComplexityLevel.High] = new List<(string, string)>
                {
                    ("Dynamic Difficulty", "Adaptive challenge scaling"),
                    ("Procedural Generation", "Algorithmic content creation"),
                    ("Complex Economy", "Supply and demand market system")
                }
            };

            if (complexityMechanics.TryGetValue(complexity, out var mechanicList))
            {
                foreach (var (name, description) in mechanicList)
                {
                    suggestions.Add(new MechanicSuggestion;
                    {
                        SuggestionId = Guid.NewGuid().ToString(),
                        GeneratedAt = DateTime.UtcNow,
                        MechanicType = InferMechanicTypeFromName(name),
                        Name = name,
                        Description = description,
                        Complexity = complexity,
                        ConfidenceScore = 0.75,
                        Source = "ComplexityTemplate",
                        Tags = new List<string> { complexity.ToString().ToLower() }
                    });
                }
            }

            return suggestions;
        }

        private void AdaptSuggestionToContext(MechanicSuggestion suggestion, MechanicCreationSession session)
        {
            // Bağlama göre adaptasyonlar;
            suggestion.Tags.Add(session.Context.GameGenre.ToLower());
            suggestion.Tags.Add(session.Context.TargetPlatform.ToLower());

            // Complexity ayarı;
            if (suggestion.Complexity > session.Context.ComplexityLevel)
            {
                suggestion.Complexity = session.Context.ComplexityLevel;
                suggestion.ImplementationNotes += $" (Complexity reduced to match session context)";
            }
        }

        private bool IsMechanicTypeAppropriateForGenre(MechanicType mechanicType, string genre)
        {
            // Genre-mechanic type eşleştirmesi;
            var genreMechanics = new Dictionary<string, List<MechanicType>>
            {
                ["RPG"] = new List<MechanicType> { MechanicType.Progression, MechanicType.Economy, MechanicType.Quest, MechanicType.Combat },
                ["FPS"] = new List<MechanicType> { MechanicType.Combat, MechanicType.Movement, MechanicType.Weapon, MechanicType.Multiplayer },
                ["Platformer"] = new List<MechanicType> { MechanicType.Movement, MechanicType.Collection, MechanicType.Puzzle, MechanicType.Environment },
                ["Strategy"] = new List<MechanicType> { MechanicType.Economy, MechanicType.Building, MechanicType.Tactical, MechanicType.Resource }
            };

            if (genreMechanics.TryGetValue(genre, out var appropriateTypes))
            {
                return appropriateTypes.Contains(mechanicType);
            }

            return true; // Bilinmeyen genre için her şey uygun;
        }

        private bool IsMechanicSuitableForPlatform(MechanicSuggestion suggestion, string platform)
        {
            // Platform kısıtlamaları;
            var platformRestrictions = new Dictionary<string, List<string>>
            {
                ["Mobile"] = new List<string> { "ComplexUI", "PreciseControls", "HeavyProcessing" },
                ["Console"] = new List<string> { "MousePrecision", "KeyboardDependent" },
                ["VR"] = new List<string> { "TraditionalUI", "2DInterface", "ScreenBound" }
            };

            if (platformRestrictions.TryGetValue(platform, out var restrictions))
            {
                // Mekanik adı veya açıklamasında kısıtlı terimleri kontrol et;
                var mechanicText = $"{suggestion.Name} {suggestion.Description}".ToLower();
                return !restrictions.Any(r => mechanicText.Contains(r.ToLower()));
            }

            return true;
        }

        private async Task<List<string>> CheckMissingDependenciesAsync(List<string> dependencies, CancellationToken cancellationToken)
        {
            var missing = new List<string>();

            if (dependencies == null || !dependencies.Any())
                return missing;

            // Basit dependency kontrolü;
            // Gerçek implementasyonda dependency graph kontrolü yapılacak;
            var existingSystems = new List<string> { "PhysicsSystem", "InputSystem", "AudioSystem", "UISystem", "AnimationSystem" };

            foreach (var dep in dependencies)
            {
                if (!existingSystems.Contains(dep) && !dep.StartsWith("Common"))
                {
                    missing.Add(dep);
                }
            }

            return await Task.FromResult(missing);
        }

        private async Task<IEnumerable<GameMechanic>> FindSimilarExistingMechanicsAsync(
            MechanicSuggestion suggestion,
            CancellationToken cancellationToken)
        {
            // Benzer mekanikleri bul (isim, açıklama veya özelliklere göre)
            var query = _mechanicRepository.GetAll()
                .Where(m => m.Name.Contains(suggestion.Name) ||
                           m.Description.Contains(suggestion.Name) ||
                           m.KeyFeatures.Any(f => suggestion.KeyFeatures?.Contains(f) == true))
                .Where(m => m.Status != MechanicStatus.Rejected)
                .Take(5);

            return await _templateRepository.ToListAsync(query, cancellationToken);
        }

        private void UpdateAILearningData(MechanicCreationSession session, MechanicSuggestion suggestion)
        {
            // AI öğrenme verilerini güncelle;
            if (!session.SessionData.ContainsKey("ai_learning_data"))
            {
                session.SessionData["ai_learning_data"] = new AILearningData;
                {
                    SessionId = session.SessionId,
                    GeneratedSuggestions = new List<MechanicSuggestion>(),
                    AcceptedSuggestions = new List<string>(),
                    RejectedSuggestions = new List<string>(),
                    Feedback = new Dictionary<string, double>()
                };
            }

            var learningData = (AILearningData)session.SessionData["ai_learning_data"];
            learningData.GeneratedSuggestions.Add(suggestion);

            // Feedback tracking;
            learningData.Feedback[suggestion.SuggestionId] = suggestion.ConfidenceScore;
        }

        private async Task AdaptForPlatformAsync(GameMechanic mechanic, string platform, CancellationToken cancellationToken)
        {
            // Platform-specific adaptasyonlar;
            switch (platform.ToLower())
            {
                case "mobile":
                    mechanic.KeyFeatures.Add("Touch-optimized");
                    mechanic.KeyFeatures.Add("Battery-efficient");
                    mechanic.PerformanceBudget = PerformanceBudget.Low;
                    break;

                case "console":
                    mechanic.KeyFeatures.Add("Controller-optimized");
                    mechanic.KeyFeatures.Add("Console-certified");
                    break;

                case "vr":
                    mechanic.KeyFeatures.Add("VR-compatible");
                    mechanic.KeyFeatures.Add("Motion-sickness aware");
                    mechanic.PerformanceBudget = PerformanceBudget.High;
                    break;

                case "pc":
                    mechanic.KeyFeatures.Add("Keyboard/mouse support");
                    mechanic.KeyFeatures.Add("Mod-friendly");
                    break;
            }

            await Task.CompletedTask;
        }

        private async Task AdaptForGenreAsync(GameMechanic mechanic, string genre, CancellationToken cancellationToken)
        {
            // Genre-specific adaptasyonlar;
            mechanic.Tags.Add(genre.ToLower());

            switch (genre.ToLower())
            {
                case "rpg":
                    if (!mechanic.KeyFeatures.Contains("Progression"))
                        mechanic.KeyFeatures.Add("Progression-oriented");
                    break;

                case "fps":
                    if (!mechanic.KeyFeatures.Contains("Fast-paced"))
                        mechanic.KeyFeatures.Add("Fast-paced");
                    break;

                case "strategy":
                    if (!mechanic.KeyFeatures.Contains("Strategic depth"))
                        mechanic.KeyFeatures.Add("Strategic depth");
                    break;

                case "puzzle":
                    if (!mechanic.KeyFeatures.Contains("Logic-based"))
                        mechanic.KeyFeatures.Add("Logic-based");
                    break;
            }

            await Task.CompletedTask;
        }

        private void AdjustForComplexity(GameMechanic mechanic, ComplexityLevel targetComplexity)
        {
            if (mechanic.Complexity > targetComplexity)
            {
                // Complexity'i düşür;
                mechanic.Complexity = targetComplexity;
                mechanic.ImplementationNotes += $" [Complexity reduced from {mechanic.Complexity} to {targetComplexity}]";

                // Complexity'i düşürmek için bazı özellikleri basitleştir;
                SimplifyForComplexity(mechanic, targetComplexity);
            }
        }

        private void SimplifyForComplexity(GameMechanic mechanic, ComplexityLevel targetComplexity)
        {
            // Complexity'e göre basitleştirme kuralları;
            if (targetComplexity <= ComplexityLevel.Low)
            {
                // Çok karmaşık parametreleri kaldır;
                mechanic.Parameters = mechanic.Parameters?
                    .Where(p => !p.Name.Contains("Advanced") && !p.Name.Contains("Complex"))
                    .ToList();

                // Gelişmiş özellikleri kaldır;
                mechanic.KeyFeatures = mechanic.KeyFeatures;
                    .Where(f => !f.Contains("Advanced") && !f.Contains("Complex"))
                    .ToList();
            }
        }

        private double CalculateGoalAlignment(GameMechanic mechanic, List<string> designGoals)
        {
            if (!designGoals.Any())
                return 1.0;

            var alignmentScores = new List<double>();

            foreach (var goal in designGoals)
            {
                double score = 0;

                // Goal'a göre puanlama;
                if (goal.Contains("engagement") || goal.Contains("retention"))
                {
                    if (mechanic.KeyFeatures.Any(f => f.Contains("Progression") || f.Contains("Reward")))
                        score += 0.3;
                    if (mechanic.Type == MechanicType.Progression || mechanic.Type == MechanicType.Reward)
                        score += 0.3;
                }

                if (goal.Contains("accessibility") || goal.Contains("easy to learn"))
                {
                    if (mechanic.Complexity <= ComplexityLevel.Medium)
                        score += 0.4;
                    if (mechanic.KeyFeatures.Any(f => f.Contains("Simple") || f.Contains("Intuitive")))
                        score += 0.3;
                }

                if (goal.Contains("depth") || goal.Contains("complexity"))
                {
                    if (mechanic.Complexity >= ComplexityLevel.Medium)
                        score += 0.4;
                    if (mechanic.KeyFeatures.Any(f => f.Contains("Deep") || f.Contains("Strategic")))
                        score += 0.3;
                }

                alignmentScores.Add(Math.Min(score, 1.0));
            }

            return alignmentScores.Average();
        }

        private void ApplyConstraints(GameMechanic mechanic, Dictionary<string, object> constraints)
        {
            foreach (var constraint in constraints)
            {
                switch (constraint.Key.ToLower())
                {
                    case "max_development_time":
                        if (constraint.Value is double maxTime && mechanic.EstimatedDevTime > maxTime)
                        {
                            mechanic.EstimatedDevTime = maxTime;
                            mechanic.ImplementationNotes += $" [Dev time limited to {maxTime} hours by constraint]";
                        }
                        break;

                    case "required_performance":
                        if (constraint.Value is string perfReq)
                        {
                            mechanic.PerformanceRequirements = perfReq;
                        }
                        break;

                    case "platform_limitations":
                        if (constraint.Value is List<string> limitations)
                        {
                            mechanic.PlatformLimitations = limitations;
                        }
                        break;
                }
            }
        }

        private SystemArchitecture GenerateSystemArchitecture(GameMechanic mechanic)
        {
            return new SystemArchitecture;
            {
                MainSystem = $"{mechanic.Type}System",
                Subsystems = GenerateSubsystems(mechanic),
                DataFlow = GenerateDataFlow(mechanic),
                CommunicationPattern = DetermineCommunicationPattern(mechanic),
                Scalability = DetermineScalability(mechanic)
            };
        }

        private CodeStructure GenerateCodeStructure(GameMechanic mechanic)
        {
            return new CodeStructure;
            {
                MainClass = $"{mechanic.Type}Mechanic",
                Interfaces = GenerateInterfaces(mechanic),
                Components = GenerateComponents(mechanic),
                DataModels = GenerateDataModels(mechanic),
                Configuration = GenerateConfiguration(mechanic)
            };
        }

        private List<RequiredComponent> GenerateRequiredComponents(GameMechanic mechanic)
        {
            var components = new List<RequiredComponent>();

            // Type'a göre bileşenler;
            switch (mechanic.Type)
            {
                case MechanicType.Movement:
                    components.Add(new RequiredComponent { Name = "MovementController", Type = "Script" });
                    components.Add(new RequiredComponent { Name = "PhysicsBody", Type = "Component" });
                    components.Add(new RequiredComponent { Name = "InputHandler", Type = "System" });
                    break;

                case MechanicType.Combat:
                    components.Add(new RequiredComponent { Name = "DamageSystem", Type = "System" });
                    components.Add(new RequiredComponent { Name = "HitDetection", Type = "Component" });
                    components.Add(new RequiredComponent { Name = "CombatUI", Type = "UI" });
                    break;

                case MechanicType.Economy:
                    components.Add(new RequiredComponent { Name = "CurrencyManager", Type = "System" });
                    components.Add(new RequiredComponent { Name = "TransactionSystem", Type = "System" });
                    components.Add(new RequiredComponent { Name = "EconomyUI", Type = "UI" });
                    break;
            }

            return components;
        }

        private List<IntegrationPoint> GenerateIntegrationPoints(GameMechanic mechanic)
        {
            var points = new List<IntegrationPoint>();

            // Ortak integration noktaları;
            points.Add(new IntegrationPoint;
            {
                System = "GameStateManager",
                Purpose = "Mechanic state synchronization",
                Interface = "IGameStateListener"
            });

            points.Add(new IntegrationPoint;
            {
                System = "EventSystem",
                Purpose = "Mechanic event broadcasting",
                Interface = "IEventPublisher"
            });

            points.Add(new IntegrationPoint;
            {
                System = "SaveSystem",
                Purpose = "Mechanic data persistence",
                Interface = "ISaveable"
            });

            return points;
        }

        private TestingStrategy GenerateTestingStrategy(GameMechanic mechanic)
        {
            return new TestingStrategy;
            {
                UnitTests = GenerateUnitTestCases(mechanic),
                IntegrationTests = GenerateIntegrationTestCases(mechanic),
                PerformanceTests = GeneratePerformanceTestCases(mechanic),
                TestDataRequirements = GenerateTestDataRequirements(mechanic)
            };
        }

        private async Task<List<AssetRequirement>> GenerateAssetRequirementsAsync(GameMechanic mechanic, CancellationToken cancellationToken)
        {
            var requirements = new List<AssetRequirement>();

            // Type'a göre asset gereksinimleri;
            switch (mechanic.Type)
            {
                case MechanicType.Movement:
                    requirements.Add(new AssetRequirement { Type = "Animation", Quantity = 3, Priority = "High" });
                    requirements.Add(new AssetRequirement { Type = "Sound", Quantity = 5, Priority = "Medium" });
                    break;

                case MechanicType.Combat:
                    requirements.Add(new AssetRequirement { Type = "VFX", Quantity = 10, Priority = "High" });
                    requirements.Add(new AssetRequirement { Type = "SFX", Quantity = 15, Priority = "High" });
                    requirements.Add(new AssetRequirement { Type = "Animation", Quantity = 8, Priority = "High" });
                    break;

                case MechanicType.UI:
                    requirements.Add(new AssetRequirement { Type = "UI Art", Quantity = 20, Priority = "High" });
                    requirements.Add(new AssetRequirement { Type = "Icon", Quantity = 10, Priority = "Medium" });
                    break;
            }

            return await Task.FromResult(requirements);
        }

        private List<PerformanceConsideration> GeneratePerformanceConsiderations(GameMechanic mechanic)
        {
            var considerations = new List<PerformanceConsideration>();

            // Complexity'e göre performans düşünceleri;
            if (mechanic.Complexity >= ComplexityLevel.High)
            {
                considerations.Add(new PerformanceConsideration;
                {
                    Area = "CPU",
                    Concern = "High complexity may cause CPU spikes",
                    Mitigation = "Implement level of detail or async processing"
                });
            }

            if (mechanic.KeyFeatures.Any(f => f.Contains("Real-time") || f.Contains("Dynamic")))
            {
                considerations.Add(new PerformanceConsideration;
                {
                    Area = "Update Loop",
                    Concern = "Frequent updates may impact frame rate",
                    Mitigation = "Use coroutines or fixed update intervals"
                });
            }

            if (mechanic.Parameters?.Count > 10)
            {
                considerations.Add(new PerformanceConsideration;
                {
                    Area = "Memory",
                    Concern = "Many parameters may increase memory usage",
                    Mitigation = "Use structs or object pooling for parameters"
                });
            }

            return considerations;
        }

        private async Task AdjustTemplateComplexityAsync(MechanicTemplate template, ComplexityLevel targetComplexity, CancellationToken cancellationToken)
        {
            // Şablon complexity'ini ayarla;
            if (template.TemplateData?.Parameters != null)
            {
                foreach (var param in template.TemplateData.Parameters.Values)
                {
                    // Complexity'e göre parametre aralıklarını ayarla;
                    if (targetComplexity <= ComplexityLevel.Low)
                    {
                        param.Min = Math.Max(param.Min, param.DefaultValue * 0.5);
                        param.Max = Math.Min(param.Max, param.DefaultValue * 1.5);
                    }
                    else if (targetComplexity >= ComplexityLevel.High)
                    {
                        param.Min = Math.Min(param.Min, param.DefaultValue * 0.1);
                        param.Max = Math.Max(param.Max, param.DefaultValue * 3.0);
                    }
                }
            }

            await Task.CompletedTask;
        }

        private async Task<CompatibilityResult> CheckMechanicCompatibilityAsync(
            GameMechanic mech1,
            GameMechanic mech2,
            CancellationToken cancellationToken)
        {
            // Basit uyumluluk kontrolü;
            var result = new CompatibilityResult;
            {
                IsCompatible = true,
                SynergyPotential = 0.5,
                Reason = "Generally compatible"
            };

            // Açık çakışmaları kontrol et;
            var conflictingFeatures = mech1.KeyFeatures;
                .Intersect(mech2.KeyFeatures)
                .Where(f => f.Contains("Exclusive") || f.Contains("Unique"))
                .ToList();

            if (conflictingFeatures.Any())
            {
                result.IsCompatible = false;
                result.Reason = $"Conflicting features: {string.Join(", ", conflictingFeatures)}";
                return result;
            }

            // Dependency çakışmalarını kontrol et;
            var commonDeps = mech1.Dependencies?.Intersect(mech2.Dependencies ?? new List<string>()).ToList();
            if (commonDeps?.Any(d => d.Contains("Singleton") || d.Contains("Global")) == true)
            {
                result.Warnings.Add("Shared singleton dependencies may cause conflicts");
            }

            // Sinerji potansiyelini hesapla;
            if (AreMechanicsSynergistic(mech1, mech2))
            {
                result.SynergyPotential = 0.8;
                result.Reason += " (High synergy potential)";
            }

            return await Task.FromResult(result);
        }

        private bool AreMechanicsSynergistic(GameMechanic mech1, GameMechanic mech2)
        {
            // Sinerji kontrolü;
            var synergisticPairs = new List<(MechanicType, MechanicType)>
            {
                (MechanicType.Movement, MechanicType.Combat),
                (MechanicType.Economy, MechanicType.Progression),
                (MechanicType.Quest, MechanicType.Reward),
                (MechanicType.Building, MechanicType.Resource)
            };

            return synergisticPairs.Contains((mech1.Type, mech2.Type)) ||
                   synergisticPairs.Contains((mech2.Type, mech1.Type));
        }

        private async Task<ResourceEstimate> EstimateCombinedResourceRequirementsAsync(
            List<GameMechanic> mechanics,
            CancellationToken cancellationToken)
        {
            var estimate = new ResourceEstimate;
            {
                DevelopmentTime = mechanics.Sum(m => m.EstimatedDevTime) * 0.7, // Kombinasyon verimliliği;
                CodeComplexity = mechanics.Average(m => (int)m.Complexity) * 1.3,
                AssetCount = mechanics.Sum(m => m.AssetRequirements?.Sum(a => a.Quantity) ?? 0),
                MemoryUsage = mechanics.Count * 10, // MB cinsinden tahmini;
                PerformanceImpact = CalculatePerformanceImpact(mechanics)
            };

            return await Task.FromResult(estimate);
        }

        private PerformanceImpact CalculatePerformanceImpact(List<GameMechanic> mechanics)
        {
            var avgComplexity = mechanics.Average(m => (int)m.Complexity);

            if (avgComplexity >= 4) // VeryHigh;
                return PerformanceImpact.High;
            else if (avgComplexity >= 3) // High;
                return PerformanceImpact.Medium;
            else;
                return PerformanceImpact.Low;
        }

        private MechanicType DetermineCombinedType(List<GameMechanic> mechanics)
        {
            // En yaygın type'ı bul veya hybrid belirle;
            var typeCounts = mechanics;
                .GroupBy(m => m.Type)
                .OrderByDescending(g => g.Count())
                .ToList();

            if (typeCounts.Count == 1)
                return typeCounts[0].Key;

            // Birden fazla type varsa hybrid;
            if (typeCounts[0].Count() > mechanics.Count / 2)
                return typeCounts[0].Key;
            else;
                return MechanicType.Hybrid;
        }

        private string CombineCoreConcepts(List<GameMechanic> mechanics)
        {
            var concepts = mechanics.Select(m => m.CoreMechanic).Distinct();
            return $"Combination of: {string.Join(" + ", concepts)}";
        }

        private async Task<List<MechanicParameter>> CombineParametersAsync(
            List<GameMechanic> mechanics,
            CancellationToken cancellationToken)
        {
            var combinedParams = new Dictionary<string, MechanicParameter>();

            foreach (var mechanic in mechanics)
            {
                if (mechanic.Parameters == null) continue;

                foreach (var param in mechanic.Parameters)
                {
                    if (combinedParams.ContainsKey(param.Name))
                    {
                        // Conflict resolution;
                        var existing = combinedParams[param.Name];
                        existing.Value = ResolveParameterConflict(existing, param);
                        existing.SourceMechanics.Add(mechanic.Id);
                    }
                    else;
                    {
                        var newParam = param.Clone();
                        newParam.SourceMechanics = new List<string> { mechanic.Id };
                        combinedParams[param.Name] = newParam;
                    }
                }
            }

            return combinedParams.Values.ToList();
        }

        private object ResolveParameterConflict(MechanicParameter param1, MechanicParameter param2)
        {
            // Conflict resolution stratejileri;
            if (param1.DataType == typeof(double) && param2.DataType == typeof(double))
            {
                // Ortalama al;
                var val1 = Convert.ToDouble(param1.Value);
                var val2 = Convert.ToDouble(param2.Value);
                return (val1 + val2) / 2;
            }
            else if (param1.DataType == typeof(int) && param2.DataType == typeof(int))
            {
                // Maksimum değer;
                var val1 = Convert.ToInt32(param1.Value);
                var val2 = Convert.ToInt32(param2.Value);
                return Math.Max(val1, val2);
            }
            else;
            {
                // İlk değeri koru;
                return param1.Value;
            }
        }

        private double CalculateActiveTime(MechanicCreationSession session)
        {
            if (!session.History.Any())
                return 0;

            var firstStep = session.History.Min(h => h.Timestamp);
            var lastStep = session.History.Max(h => h.Timestamp);

            return (lastStep - firstStep).TotalSeconds;
        }

        private double CalculateValidationPassRate(MechanicCreationSession session)
        {
            if (session.CreatedMechanics.Count == 0)
                return 0;

            var validCount = session.CreatedMechanics.Count(m => m.ValidationResult?.IsValid != false);
            return (double)validCount / session.CreatedMechanics.Count;
        }

        private double CalculateAIAcceptanceRate(MechanicCreationSession session)
        {
            var aiSuggestions = session.Suggestions?.Count(s => s.Source == "AI") ?? 0;
            if (aiSuggestions == 0)
                return 0;

            var acceptedSuggestions = session.CreatedMechanics.Count(m => !string.IsNullOrEmpty(m.SourceSuggestionId));
            return (double)acceptedSuggestions / aiSuggestions;
        }

        private Type InferDataType(object value)
        {
            if (value == null) return typeof(object);

            return value.GetType();
        }

        private MechanicType InferMechanicTypeFromName(string name)
        {
            // İsimden mekanik type'ını tahmin et;
            if (name.Contains("Movement") || name.Contains("Move") || name.Contains("Jump"))
                return MechanicType.Movement;
            else if (name.Contains("Combat") || name.Contains("Attack") || name.Contains("Damage"))
                return MechanicType.Combat;
            else if (name.Contains("Economy") || name.Contains("Currency") || name.Contains("Trade"))
                return MechanicType.Economy;
            else if (name.Contains("UI") || name.Contains("Interface") || name.Contains("Menu"))
                return MechanicType.UI;
            else;
                return MechanicType.Core;
        }

        private List<string> GenerateSubsystems(GameMechanic mechanic)
        {
            // Mekanik type'ına göre alt sistemler;
            return mechanic.Type switch;
            {
                MechanicType.Movement => new List<string> { "InputHandler", "PhysicsController", "AnimationController", "CollisionSystem" },
                MechanicType.Combat => new List<string> { "DamageCalculator", "HitDetection", "CombatAnimation", "EffectSystem" },
                MechanicType.Economy => new List<string> { "CurrencyManager", "TransactionHandler", "PriceCalculator", "InventorySystem" },
                _ => new List<string> { "CoreSystem", "EventSystem", "StateManager" }
            };
        }

        private List<InterfaceDefinition> GenerateInterfaces(GameMechanic mechanic)
        {
            return new List<InterfaceDefinition>
            {
                new InterfaceDefinition { Name = $"I{mechanic.Type}Mechanic", Purpose = "Main mechanic interface" },
                new InterfaceDefinition { Name = $"I{mechanic.Type}Config", Purpose = "Configuration interface" },
                new InterfaceDefinition { Name = $"I{mechanic.Type}Events", Purpose = "Event publishing interface" }
            };
        }

        private List<CodeComponent> GenerateComponents(GameMechanic mechanic)
        {
            var components = new List<CodeComponent>
            {
                new CodeComponent { Name = $"{mechanic.Type}Manager", Type = "Manager", Responsibility = "Orchestrates mechanic execution" },
                new CodeComponent { Name = $"{mechanic.Type}Config", Type = "Data", Responsibility = "Holds configuration data" },
                new CodeComponent { Name = $"{mechanic.Type}State", Type = "State", Responsibility = "Tracks mechanic state" }
            };

            return components;
        }

        private List<DataModel> GenerateDataModels(GameMechanic mechanic)
        {
            return new List<DataModel>
            {
                new DataModel { Name = $"{mechanic.Type}Data", Properties = mechanic.Parameters?.Select(p => p.Name).ToList() },
                new DataModel { Name = $"{mechanic.Type}StateData", Properties = new List<string> { "IsActive", "CurrentValue", "LastUpdated" } }
            };
        }

        private ConfigurationDefinition GenerateConfiguration(GameMechanic mechanic)
        {
            return new ConfigurationDefinition;
            {
                FileName = $"{mechanic.Type.ToLower()}_config.json",
                Structure = mechanic.Parameters?.ToDictionary(p => p.Name, p => p.Value),
                ValidationRules = new Dictionary<string, object>()
            };
        }

        private List<UnitTestCase> GenerateUnitTestCases(GameMechanic mechanic)
        {
            return new List<UnitTestCase>
            {
                new UnitTestCase { Name = $"{mechanic.Type}InitializationTest", Purpose = "Tests mechanic initialization" },
                new UnitTestCase { Name = $"{mechanic.Type}ParameterValidationTest", Purpose = "Tests parameter validation" },
                new UnitTestCase { Name = $"{mechanic.Type}StateTransitionTest", Purpose = "Tests state transitions" }
            };
        }

        private List<IntegrationTestCase> GenerateIntegrationTestCases(GameMechanic mechanic)
        {
            return new List<IntegrationTestCase>
            {
                new IntegrationTestCase {
                    Name = $"{mechanic.Type}WithGameStateTest",
                    Purpose = "Tests integration with game state system",
                    Systems = new List<string> { "GameStateManager", $"{mechanic.Type}System" }
                }
            };
        }

        private List<PerformanceTestCase> GeneratePerformanceTestCases(GameMechanic mechanic)
        {
            return new List<PerformanceTestCase>
            {
                new PerformanceTestCase {
                    Name = $"{mechanic.Type}LoadTest",
                    Purpose = "Tests mechanic under load",
                    Metrics = new List<string> { "MemoryUsage", "CPUUsage", "FrameRate" }
                }
            };
        }

        private List<TestDataRequirement> GenerateTestDataRequirements(GameMechanic mechanic)
        {
            return new List<TestDataRequirement>
            {
                new TestDataRequirement { Type = "ParameterSets", Quantity = 5, Description = "Various parameter combinations" },
                new TestDataRequirement { Type = "StateScenarios", Quantity = 3, Description = "Different state scenarios" }
            };
        }

        private DataFlow GenerateDataFlow(GameMechanic mechanic)
        {
            return new DataFlow;
            {
                Inputs = new List<string> { "UserInput", "GameState", "OtherMechanics" },
                Processing = new List<string> { "ParameterApplication", "StateUpdate", "EffectCalculation" },
                Outputs = new List<string> { "GameEvents", "StateChanges", "VisualFeedback" }
            };
        }

        private string DetermineCommunicationPattern(GameMechanic mechanic)
        {
            return mechanic.Complexity >= ComplexityLevel.High ? "Event-Driven" : "Direct Call";
        }

        private ScalabilityLevel DetermineScalability(GameMechanic mechanic)
        {
            return mechanic.Complexity switch;
            {
                ComplexityLevel.Low => ScalabilityLevel.High,
                ComplexityLevel.Medium => ScalabilityLevel.Medium,
                ComplexityLevel.High => ScalabilityLevel.Low,
                _ => ScalabilityLevel.Medium;
            };
        }

        #endregion;

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("MechanicCreator is not initialized. Call InitializeAsync first.");
        }

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _cleanupTimer?.Dispose();
                    _creationLock?.Dispose();

                    // Aktif oturumları temizle;
                    foreach (var session in _activeSessions.Values)
                    {
                        ClearSessionData(session);
                    }
                    _activeSessions.Clear();

                    _logger.LogInformation("MechanicCreator disposed");
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~MechanicCreator()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Interfaces and Supporting Classes;

    public interface IMechanicCreator;
    {
        Task InitializeAsync(CancellationToken cancellationToken = default);
        Task<MechanicCreationSession> StartCreationSessionAsync(CreationSessionRequest request, CancellationToken cancellationToken = default);
        Task<MechanicSuggestion> GenerateAISuggestionAsync(string sessionId, SuggestionRequest request, CancellationToken cancellationToken = default);
        Task<GameMechanic> CreateMechanicFromSuggestionAsync(string sessionId, string suggestionId, MechanicCustomization customization = null, CancellationToken cancellationToken = default);
        Task<GameMechanic> CreateMechanicFromTemplateAsync(string sessionId, string templateId, TemplateParameters parameters, CancellationToken cancellationToken = default);
        Task<GameMechanic> CloneAndModifyMechanicAsync(string sessionId, string sourceMechanicId, ModificationRequest modification, CancellationToken cancellationToken = default);
        Task<GameMechanic> CombineMechanicsAsync(string sessionId, CombinationRequest combination, CancellationToken cancellationToken = default);
        Task<CreationSessionResult> EndCreationSessionAsync(string sessionId, SessionEndRequest endRequest = null, CancellationToken cancellationToken = default);
        Task<IEnumerable<MechanicCreationSession>> GetActiveSessionsAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<MechanicTemplate>> GetTemplatesAsync(TemplateFilter filter = null, CancellationToken cancellationToken = default);

        event EventHandler<MechanicCreatedEventArgs> OnMechanicCreated;
        event EventHandler<MechanicValidationFailedEventArgs> OnMechanicValidationFailed;
        event EventHandler<AIRecommendationGeneratedEventArgs> OnAIRecommendationGenerated;
    }

    public class MechanicCreationConfig;
    {
        public int MaxActiveSessions { get; set; }
        public TimeSpan SessionTimeout { get; set; }
        public TimeSpan CleanupInterval { get; set; }
        public int MaxSuggestionsPerSession { get; set; }
        public bool AllowInvalidMechanics { get; set; }
        public bool AllowCreationFromInvalidSuggestions { get; set; }
        public bool AllowUnlikelyCombinations { get; set; }
        public bool CleanupSessionDataOnEnd { get; set; }
        public bool KeepModificationHistoryOnClone { get; set; }
        public double AIConfidenceThreshold { get; set; }
        public ComplexityLevel DefaultComplexity { get; set; }
    }

    public class CreationSessionRequest;
    {
        public string ProjectId { get; set; }
        public string UserId { get; set; }
        public string GameGenre { get; set; }
        public string TargetPlatform { get; set; }
        public ComplexityLevel ComplexityLevel { get; set; }
        public List<string> DesignGoals { get; set; }
        public Dictionary<string, object> Constraints { get; set; }
    }

    public class SuggestionRequest;
    {
        public SuggestionType RequestType { get; set; }
        public string FocusArea { get; set; }
        public List<string> ReferenceMechanics { get; set; }
        public Dictionary<string, object> AdditionalConstraints { get; set; }
        public string InspirationSource { get; set; }
    }

    public class MechanicCustomization;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ComplexityLevel? Complexity { get; set; }
        public List<string> AdditionalFeatures { get; set; }
        public Dictionary<string, object> ParameterOverrides { get; set; }
        public List<string> AdditionalTags { get; set; }
    }

    public class TemplateParameters;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> ParameterValues { get; set; }
        public List<string> AdditionalTags { get; set; }
    }

    public class ModificationRequest;
    {
        public NameChange NameChange { get; set; }
        public List<ParameterChange> ParameterChanges { get; set; }
        public List<string> FeatureAdditions { get; set; }
        public List<string> FeatureRemovals { get; set; }
    }

    public class CombinationRequest;
    {
        public List<string> MechanicIds { get; set; }
        public string Name { get; set; }
        public CombinationStrategy Strategy { get; set; }
        public ComplexityLevel MaxAllowedComplexity { get; set; }
        public Dictionary<string, object> AdditionalParameters { get; set; }
    }

    public class SessionEndRequest;
    {
        public SessionEndReason Reason { get; set; }
        public string Feedback { get; set; }
        public Dictionary<string, object> SessionMetrics { get; set; }
    }

    public class TemplateFilter;
    {
        public string Genre { get; set; }
        public string MechanicType { get; set; }
        public ComplexityLevel? ComplexityLevel { get; set; }
        public List<string> Platforms { get; set; }
        public List<string> Tags { get; set; }
    }

    // Events;
    public class MechanicCreatedEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public GameMechanic Mechanic { get; set; }
        public string SourceSuggestionId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MechanicValidationFailedEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public string MechanicId { get; set; }
        public ValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class AIRecommendationGeneratedEventArgs : EventArgs;
    {
        public string SessionId { get; set; }
        public MechanicSuggestion Suggestion { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Event Bus Events;
    public class MechanicCreatorInitializedEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public int TemplateCount { get; set; }
        public string ActiveModel { get; set; }
    }

    public class MechanicCreatedEvent : IEvent;
    {
        public string SessionId { get; set; }
        public string MechanicId { get; set; }
        public MechanicType MechanicType { get; set; }
        public string ProjectId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // Enums;
    public enum SessionStatus;
    {
        Active,
        Paused,
        Completed,
        Timeout,
        Cancelled;
    }

    public enum SessionEndReason;
    {
        Manual,
        Timeout,
        Completed,
        Cancelled,
        Error;
    }

    public enum StepActionType;
    {
        SessionStart,
        AISuggestionRequest,
        MechanicCreated,
        TemplateUsed,
        ModificationApplied,
        CombinationCreated,
        ValidationPerformed;
    }

    public enum SuggestionType;
    {
        NewIdea,
        Improvement,
        Variation,
        Combination,
        Optimization;
    }

    public enum CombinationStrategy;
    {
        Merge,
        Chain,
        Layer,
        Hybrid,
        Custom;
    }

    public enum PerformanceBudget;
    {
        VeryLow,
        Low,
        Medium,
        High,
        VeryHigh;
    }

    public enum PerformanceImpact;
    {
        Negligible,
        Low,
        Medium,
        High,
        Critical;
    }

    public enum ScalabilityLevel;
    {
        Low,
        Medium,
        High;
    }

    // Exceptions;
    public class MechanicCreatorException : Exception
    {
        public MechanicCreatorException(string message) : base(message) { }
        public MechanicCreatorException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class SessionNotFoundException : MechanicCreatorException;
    {
        public SessionNotFoundException(string message) : base(message) { }
    }

    public class InvalidSessionStateException : MechanicCreatorException;
    {
        public InvalidSessionStateException(string message) : base(message) { }
    }

    public class SuggestionNotFoundException : MechanicCreatorException;
    {
        public SuggestionNotFoundException(string message) : base(message) { }
    }

    public class InvalidSuggestionException : MechanicCreatorException;
    {
        public InvalidSuggestionException(string message) : base(message) { }
    }

    public class AISuggestionException : MechanicCreatorException;
    {
        public AISuggestionException(string message) : base(message) { }
        public AISuggestionException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MechanicCreationException : MechanicCreatorException;
    {
        public MechanicCreationException(string message) : base(message) { }
        public MechanicCreationException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MechanicValidationException : MechanicCreatorException;
    {
        public MechanicValidationException(string message) : base(message) { }
    }

    public class TemplateNotFoundException : MechanicCreatorException;
    {
        public TemplateNotFoundException(string message) : base(message) { }
    }

    public class TemplateLoadingException : MechanicCreatorException;
    {
        public TemplateLoadingException(string message) : base(message) { }
        public TemplateLoadingException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class MechanicNotFoundException : MechanicCreatorException;
    {
        public MechanicNotFoundException(string message) : base(message) { }
    }

    public class CombinationNotPossibleException : MechanicCreatorException;
    {
        public CombinationNotPossibleException(string message) : base(message) { }
    }

    public class MLModelException : MechanicCreatorException;
    {
        public MLModelException(string message) : base(message) { }
        public MLModelException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
