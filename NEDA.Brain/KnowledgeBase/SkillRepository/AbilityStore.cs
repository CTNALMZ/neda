using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.Core.Common;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Brain.KnowledgeBase.SkillRepository;
{
    /// <summary>
    /// Yetenek depolama ve yönetim sistemi.
    /// NEDA'nın sahip olduğu tüm yetenekleri depolar, organize eder ve erişim sağlar.
    /// </summary>
    public interface IAbilityStore;
    {
        /// <summary>
        /// Yeni bir yetenek ekler.
        /// </summary>
        Task<Ability> AddAbilityAsync(Ability ability);

        /// <summary>
        /// Yetenek ID'sine göre yetenek getirir.
        /// </summary>
        Task<Ability> GetAbilityByIdAsync(Guid abilityId);

        /// <summary>
        /// Yetenek adına göre yetenek getirir.
        /// </summary>
        Task<Ability> GetAbilityByNameAsync(string abilityName);

        /// <summary>
        /// Kategoriye göre yetenekleri listeler.
        /// </summary>
        Task<IEnumerable<Ability>> GetAbilitiesByCategoryAsync(AbilityCategory category);

        /// <summary>
        /// Tüm yetenekleri listeler.
        /// </summary>
        Task<IEnumerable<Ability>> GetAllAbilitiesAsync();

        /// <summary>
        /// Yetenek günceller.
        /// </summary>
        Task<Ability> UpdateAbilityAsync(Ability ability);

        /// <summary>
        /// Yetenek siler.
        /// </summary>
        Task<bool> RemoveAbilityAsync(Guid abilityId);

        /// <summary>
        /// Yetenek seviyesini artırır.
        /// </summary>
        Task<Ability> IncreaseAbilityLevelAsync(Guid abilityId, int levelIncrease = 1);

        /// <summary>
        /// Yetenek kullanım sayısını artırır.
        /// </summary>
        Task<Ability> RecordAbilityUsageAsync(Guid abilityId, UsageContext context);

        /// <summary>
        /// Belirli bir görev için en uygun yetenekleri önerir.
        /// </summary>
        Task<IEnumerable<AbilityRecommendation>> RecommendAbilitiesAsync(TaskDescription task);

        /// <summary>
        /// Yetenekleri karmaşıklık seviyesine göre filtreler.
        /// </summary>
        Task<IEnumerable<Ability>> GetAbilitiesByComplexityAsync(AbilityComplexity complexity);

        /// <summary>
        /// Bağımlı yetenekleri kontrol eder.
        /// </summary>
        Task<IEnumerable<Ability>> GetPrerequisiteAbilitiesAsync(Guid abilityId);

        /// <summary>
        /// Yetenek durumunu kontrol eder.
        /// </summary>
        Task<AbilityStatus> CheckAbilityStatusAsync(Guid abilityId);

        /// <summary>
        /// Yetenek öğrenme durumunu günceller.
        /// </summary>
        Task<Ability> UpdateLearningProgressAsync(Guid abilityId, LearningProgress progress);
    }

    /// <summary>
    /// Yetenek depolama implementasyonu.
    /// </summary>
    public class AbilityStore : IAbilityStore, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly Dictionary<Guid, Ability> _abilities;
        private readonly Dictionary<string, Guid> _abilityNameIndex;
        private readonly object _syncLock = new object();
        private bool _disposed;
        private readonly TimeSpan _cacheDuration = TimeSpan.FromMinutes(30);

        /// <summary>
        /// Yetenek deposu sınıfı.
        /// </summary>
        public AbilityStore(
            ILogger logger,
            IEventBus eventBus,
            IKnowledgeGraph knowledgeGraph)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));

            _abilities = new Dictionary<Guid, Ability>();
            _abilityNameIndex = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            InitializeStoreAsync().ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Depoyu başlangıç yetenekleriyle başlatır.
        /// </summary>
        private async Task InitializeStoreAsync()
        {
            try
            {
                var coreAbilities = GetCoreAbilities();
                foreach (var ability in coreAbilities)
                {
                    await AddAbilityInternalAsync(ability, false);
                }

                _logger.Information("AbilityStore initialized with {AbilityCount} core abilities", coreAbilities.Count());

                await _eventBus.PublishAsync(new AbilityStoreInitializedEvent;
                {
                    Timestamp = DateTime.UtcNow,
                    AbilityCount = coreAbilities.Count()
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to initialize AbilityStore");
                throw new AbilityStoreInitializationException(
                    "Failed to initialize ability store",
                    ex,
                    ErrorCodes.AbilityStore.InitializationFailed);
            }
        }

        /// <summary>
        /// Temel yetenekleri tanımlar.
        /// </summary>
        private IEnumerable<Ability> GetCoreAbilities()
        {
            return new List<Ability>
            {
                new Ability;
                {
                    Id = Guid.Parse("A1B2C3D4-E5F6-4789-ABCD-012345678901"),
                    Name = "NaturalLanguageProcessing",
                    DisplayName = "Doğal Dil İşleme",
                    Description = "İnsan dilini anlama ve işleme yeteneği",
                    Category = AbilityCategory.Language,
                    Complexity = AbilityComplexity.Advanced,
                    BaseLevel = 8,
                    CurrentLevel = 8,
                    MaxLevel = 10,
                    UsageCount = 0,
                    LastUsed = DateTime.UtcNow,
                    IsActive = true,
                    PrerequisiteIds = new List<Guid>(),
                    Tags = new List<string> { "NLP", "AI", "Language", "TextAnalysis" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "SupportedLanguages", new[] { "tr", "en", "es", "fr", "de" } },
                        { "ProcessingSpeed", "1000 tokens/second" },
                        { "Accuracy", 0.94 }
                    }
                },
                new Ability;
                {
                    Id = Guid.Parse("B2C3D4E5-F6A7-5890-BCDE-123456789012"),
                    Name = "CodeGeneration",
                    DisplayName = "Kod Üretimi",
                    Description = "Çeşitli programlama dillerinde kod üretme yeteneği",
                    Category = AbilityCategory.Development,
                    Complexity = AbilityComplexity.Expert,
                    BaseLevel = 9,
                    CurrentLevel = 9,
                    MaxLevel = 10,
                    UsageCount = 0,
                    LastUsed = DateTime.UtcNow,
                    IsActive = true,
                    PrerequisiteIds = new List<Guid>(),
                    Tags = new List<string> { "Coding", "Development", "Programming", "CodeGen" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "SupportedLanguages", new[] { "C#", "Python", "JavaScript", "Java", "C++" } },
                        { "CodeQuality", "ProductionReady" },
                        { "FrameworkSupport", new[] { ".NET", "React", "Unity", "Unreal" } }
                    }
                },
                new Ability;
                {
                    Id = Guid.Parse("C3D4E5F6-A7B8-6901-CDEF-234567890123"),
                    Name = "DecisionMaking",
                    DisplayName = "Karar Verme",
                    Description = "Karmaşık durumlarda mantıklı kararlar verme yeteneği",
                    Category = AbilityCategory.Cognitive,
                    Complexity = AbilityComplexity.Advanced,
                    BaseLevel = 7,
                    CurrentLevel = 7,
                    MaxLevel = 10,
                    UsageCount = 0,
                    LastUsed = DateTime.UtcNow,
                    IsActive = true,
                    PrerequisiteIds = new List<Guid>(),
                    Tags = new List<string> { "Decision", "Logic", "Analysis", "ProblemSolving" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "DecisionSpeed", "50ms" },
                        { "Accuracy", 0.87 },
                        { "RiskAssessment", "Enabled" }
                    }
                },
                new Ability;
                {
                    Id = Guid.Parse("D4E5F6A7-B8C9-7012-DEF0-345678901234"),
                    Name = "PatternRecognition",
                    DisplayName = "Örüntü Tanıma",
                    Description = "Verilerdeki örüntüleri tanıma ve analiz etme yeteneği",
                    Category = AbilityCategory.Analytical,
                    Complexity = AbilityComplexity.Intermediate,
                    BaseLevel = 6,
                    CurrentLevel = 6,
                    MaxLevel = 10,
                    UsageCount = 0,
                    LastUsed = DateTime.UtcNow,
                    IsActive = true,
                    PrerequisiteIds = new List<Guid>(),
                    Tags = new List<string> { "Patterns", "Analysis", "Data", "Recognition" },
                    Metadata = new Dictionary<string, object>
                    {
                        { "PatternTypes", new[] { "Temporal", "Spatial", "Behavioral", "Statistical" } },
                        { "RecognitionAccuracy", 0.91 },
                        { "ProcessingMode", "RealTime" }
                    }
                }
            };
        }

        /// <inheritdoc/>
        public async Task<Ability> AddAbilityAsync(Ability ability)
        {
            ValidateAbility(ability);

            try
            {
                var result = await AddAbilityInternalAsync(ability, true);

                await _eventBus.PublishAsync(new AbilityAddedEvent;
                {
                    AbilityId = result.Id,
                    AbilityName = result.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Information("Ability added: {AbilityName} ({AbilityId})",
                    ability.Name, ability.Id);

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to add ability: {AbilityName}", ability.Name);
                throw new AbilityStoreException(
                    $"Failed to add ability: {ability.Name}",
                    ex,
                    ErrorCodes.AbilityStore.AddFailed);
            }
        }

        private async Task<Ability> AddAbilityInternalAsync(Ability ability, bool validatePrerequisites)
        {
            lock (_syncLock)
            {
                if (_abilityNameIndex.ContainsKey(ability.Name))
                {
                    throw new AbilityStoreException(
                        $"Ability with name '{ability.Name}' already exists",
                        ErrorCodes.AbilityStore.DuplicateAbility);
                }

                if (ability.Id == Guid.Empty)
                {
                    ability.Id = Guid.NewGuid();
                }

                if (_abilities.ContainsKey(ability.Id))
                {
                    throw new AbilityStoreException(
                        $"Ability with ID '{ability.Id}' already exists",
                        ErrorCodes.AbilityStore.DuplicateAbility);
                }

                ability.CreatedAt = DateTime.UtcNow;
                ability.UpdatedAt = DateTime.UtcNow;

                if (validatePrerequisites && ability.PrerequisiteIds?.Any() == true)
                {
                    var missingPrerequisites = ability.PrerequisiteIds;
                        .Where(id => !_abilities.ContainsKey(id))
                        .ToList();

                    if (missingPrerequisites.Any())
                    {
                        throw new AbilityStoreException(
                            $"Missing prerequisites: {string.Join(", ", missingPrerequisites)}",
                            ErrorCodes.AbilityStore.MissingPrerequisites);
                    }
                }

                _abilities[ability.Id] = ability.Clone();
                _abilityNameIndex[ability.Name] = ability.Id;

                // Knowledge Graph'e ekle;
                _knowledgeGraph.AddEntity(new KnowledgeEntity;
                {
                    Id = ability.Id,
                    Type = "Ability",
                    Name = ability.Name,
                    Properties = new Dictionary<string, object>
                    {
                        { "DisplayName", ability.DisplayName },
                        { "Category", ability.Category.ToString() },
                        { "Complexity", ability.Complexity.ToString() }
                    }
                });
            }

            return ability;
        }

        /// <inheritdoc/>
        public async Task<Ability> GetAbilityByIdAsync(Guid abilityId)
        {
            if (abilityId == Guid.Empty)
            {
                throw new ArgumentException("Ability ID cannot be empty", nameof(abilityId));
            }

            try
            {
                lock (_syncLock)
                {
                    if (_abilities.TryGetValue(abilityId, out var ability))
                    {
                        return ability.Clone();
                    }
                }

                throw new AbilityNotFoundException(
                    $"Ability with ID '{abilityId}' not found",
                    ErrorCodes.AbilityStore.AbilityNotFound);
            }
            catch (Exception ex) when (!(ex is AbilityNotFoundException))
            {
                _logger.Error(ex, "Failed to get ability by ID: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to retrieve ability: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.RetrievalFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<Ability> GetAbilityByNameAsync(string abilityName)
        {
            if (string.IsNullOrWhiteSpace(abilityName))
            {
                throw new ArgumentException("Ability name cannot be empty", nameof(abilityName));
            }

            try
            {
                lock (_syncLock)
                {
                    if (_abilityNameIndex.TryGetValue(abilityName, out var abilityId))
                    {
                        if (_abilities.TryGetValue(abilityId, out var ability))
                        {
                            return ability.Clone();
                        }
                    }
                }

                throw new AbilityNotFoundException(
                    $"Ability with name '{abilityName}' not found",
                    ErrorCodes.AbilityStore.AbilityNotFound);
            }
            catch (Exception ex) when (!(ex is AbilityNotFoundException))
            {
                _logger.Error(ex, "Failed to get ability by name: {AbilityName}", abilityName);
                throw new AbilityStoreException(
                    $"Failed to retrieve ability: {abilityName}",
                    ex,
                    ErrorCodes.AbilityStore.RetrievalFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Ability>> GetAbilitiesByCategoryAsync(AbilityCategory category)
        {
            try
            {
                lock (_syncLock)
                {
                    return _abilities.Values;
                        .Where(a => a.Category == category && a.IsActive)
                        .Select(a => a.Clone())
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get abilities by category: {Category}", category);
                throw new AbilityStoreException(
                    $"Failed to retrieve abilities for category: {category}",
                    ex,
                    ErrorCodes.AbilityStore.RetrievalFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Ability>> GetAllAbilitiesAsync()
        {
            try
            {
                lock (_syncLock)
                {
                    return _abilities.Values;
                        .Select(a => a.Clone())
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get all abilities");
                throw new AbilityStoreException(
                    "Failed to retrieve all abilities",
                    ex,
                    ErrorCodes.AbilityStore.RetrievalFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<Ability> UpdateAbilityAsync(Ability ability)
        {
            ValidateAbility(ability);

            try
            {
                lock (_syncLock)
                {
                    if (!_abilities.ContainsKey(ability.Id))
                    {
                        throw new AbilityNotFoundException(
                            $"Ability with ID '{ability.Id}' not found",
                            ErrorCodes.AbilityStore.AbilityNotFound);
                    }

                    var existingAbility = _abilities[ability.Id];

                    // İsim değişikliği kontrolü;
                    if (existingAbility.Name != ability.Name)
                    {
                        if (_abilityNameIndex.ContainsKey(ability.Name))
                        {
                            throw new AbilityStoreException(
                                $"Ability name '{ability.Name}' already in use",
                                ErrorCodes.AbilityStore.DuplicateAbility);
                        }

                        _abilityNameIndex.Remove(existingAbility.Name);
                        _abilityNameIndex[ability.Name] = ability.Id;
                    }

                    ability.UpdatedAt = DateTime.UtcNow;
                    _abilities[ability.Id] = ability.Clone();
                }

                await _eventBus.PublishAsync(new AbilityUpdatedEvent;
                {
                    AbilityId = ability.Id,
                    AbilityName = ability.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Information("Ability updated: {AbilityName} ({AbilityId})",
                    ability.Name, ability.Id);

                return ability;
            }
            catch (Exception ex) when (!(ex is AbilityNotFoundException) && !(ex is AbilityStoreException))
            {
                _logger.Error(ex, "Failed to update ability: {AbilityName}", ability.Name);
                throw new AbilityStoreException(
                    $"Failed to update ability: {ability.Name}",
                    ex,
                    ErrorCodes.AbilityStore.UpdateFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<bool> RemoveAbilityAsync(Guid abilityId)
        {
            if (abilityId == Guid.Empty)
            {
                throw new ArgumentException("Ability ID cannot be empty", nameof(abilityId));
            }

            try
            {
                Ability removedAbility;

                lock (_syncLock)
                {
                    if (!_abilities.TryGetValue(abilityId, out removedAbility))
                    {
                        return false;
                    }

                    // Bağımlılık kontrolü;
                    var dependentAbilities = _abilities.Values;
                        .Where(a => a.PrerequisiteIds?.Contains(abilityId) == true)
                        .ToList();

                    if (dependentAbilities.Any())
                    {
                        throw new AbilityStoreException(
                            $"Cannot remove ability used as prerequisite by: " +
                            $"{string.Join(", ", dependentAbilities.Select(a => a.Name))}",
                            ErrorCodes.AbilityStore.HasDependents);
                    }

                    _abilities.Remove(abilityId);
                    _abilityNameIndex.Remove(removedAbility.Name);

                    // Knowledge Graph'ten kaldır;
                    _knowledgeGraph.RemoveEntity(abilityId);
                }

                await _eventBus.PublishAsync(new AbilityRemovedEvent;
                {
                    AbilityId = abilityId,
                    AbilityName = removedAbility.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Information("Ability removed: {AbilityName} ({AbilityId})",
                    removedAbility.Name, abilityId);

                return true;
            }
            catch (Exception ex) when (!(ex is AbilityStoreException))
            {
                _logger.Error(ex, "Failed to remove ability: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to remove ability: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.RemoveFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<Ability> IncreaseAbilityLevelAsync(Guid abilityId, int levelIncrease = 1)
        {
            if (levelIncrease <= 0)
            {
                throw new ArgumentException("Level increase must be positive", nameof(levelIncrease));
            }

            try
            {
                lock (_syncLock)
                {
                    if (!_abilities.TryGetValue(abilityId, out var ability))
                    {
                        throw new AbilityNotFoundException(
                            $"Ability with ID '{abilityId}' not found",
                            ErrorCodes.AbilityStore.AbilityNotFound);
                    }

                    var newLevel = ability.CurrentLevel + levelIncrease;
                    if (newLevel > ability.MaxLevel)
                    {
                        newLevel = ability.MaxLevel;
                    }

                    ability.CurrentLevel = newLevel;
                    ability.UpdatedAt = DateTime.UtcNow;
                    _abilities[abilityId] = ability;

                    return ability.Clone();
                }
            }
            catch (Exception ex) when (!(ex is AbilityNotFoundException))
            {
                _logger.Error(ex, "Failed to increase ability level: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to increase ability level: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.UpdateFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<Ability> RecordAbilityUsageAsync(Guid abilityId, UsageContext context)
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_abilities.TryGetValue(abilityId, out var ability))
                    {
                        throw new AbilityNotFoundException(
                            $"Ability with ID '{abilityId}' not found",
                            ErrorCodes.AbilityStore.AbilityNotFound);
                    }

                    ability.UsageCount++;
                    ability.LastUsed = DateTime.UtcNow;

                    // Kullanım bağlamını logla;
                    ability.UsageHistory ??= new List<UsageRecord>();
                    ability.UsageHistory.Add(new UsageRecord;
                    {
                        Timestamp = DateTime.UtcNow,
                        Context = context,
                        SuccessRate = context.SuccessRate;
                    });

                    // Tarihçeyi sınırla (son 100 kayıt)
                    if (ability.UsageHistory.Count > 100)
                    {
                        ability.UsageHistory = ability.UsageHistory;
                            .OrderByDescending(r => r.Timestamp)
                            .Take(100)
                            .ToList();
                    }

                    ability.UpdatedAt = DateTime.UtcNow;
                    _abilities[abilityId] = ability;

                    return ability.Clone();
                }
            }
            catch (Exception ex) when (!(ex is AbilityNotFoundException))
            {
                _logger.Error(ex, "Failed to record ability usage: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to record ability usage: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.UpdateFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<AbilityRecommendation>> RecommendAbilitiesAsync(TaskDescription task)
        {
            if (task == null)
            {
                throw new ArgumentNullException(nameof(task));
            }

            try
            {
                var allAbilities = await GetAllAbilitiesAsync();

                var recommendations = new List<AbilityRecommendation>();

                foreach (var ability in allAbilities.Where(a => a.IsActive))
                {
                    var score = CalculateRelevanceScore(ability, task);

                    if (score > 0)
                    {
                        recommendations.Add(new AbilityRecommendation;
                        {
                            Ability = ability,
                            RelevanceScore = score,
                            Confidence = CalculateConfidence(ability, task),
                            Reasoning = GenerateRecommendationReasoning(ability, task)
                        });
                    }
                }

                return recommendations;
                    .OrderByDescending(r => r.RelevanceScore)
                    .ThenByDescending(r => r.Ability.CurrentLevel)
                    .Take(10)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to recommend abilities for task");
                throw new AbilityStoreException(
                    "Failed to recommend abilities",
                    ex,
                    ErrorCodes.AbilityStore.RecommendationFailed);
            }
        }

        private float CalculateRelevanceScore(Ability ability, TaskDescription task)
        {
            float score = 0;

            // Kategori eşleşmesi;
            if (ability.Category.ToString().Contains(task.Category, StringComparison.OrdinalIgnoreCase))
            {
                score += 30;
            }

            // Tag eşleşmesi;
            var matchingTags = ability.Tags?.Intersect(task.Tags ?? new List<string>()).Count() ?? 0;
            score += matchingTags * 10;

            // Karmaşıklık uyumu;
            if (ability.Complexity >= task.RequiredComplexity)
            {
                score += 20;
            }

            // Seviye uygunluğu;
            if (ability.CurrentLevel >= task.MinimumLevel)
            {
                score += 25;
            }

            // Kullanım sıklığı (daha sık kullanılan yetenekler)
            score += Math.Min(ability.UsageCount / 100.0f, 15);

            return score;
        }

        private float CalculateConfidence(Ability ability, TaskDescription task)
        {
            float confidence = 0.5f; // Temel güven;

            // Seviye faktörü;
            confidence += (ability.CurrentLevel / (float)ability.MaxLevel) * 0.3f;

            // Başarı oranı;
            if (ability.UsageHistory?.Any() == true)
            {
                var successRate = ability.UsageHistory.Average(r => r.SuccessRate);
                confidence += successRate * 0.2f;
            }

            return Math.Min(confidence, 1.0f);
        }

        private string GenerateRecommendationReasoning(Ability ability, TaskDescription task)
        {
            var reasons = new List<string>();

            if (ability.Category.ToString().Contains(task.Category, StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add($"Kategori uyumu: {ability.Category}");
            }

            var matchingTags = ability.Tags?.Intersect(task.Tags ?? new List<string>()).ToList();
            if (matchingTags?.Any() == true)
            {
                reasons.Add($"Tag eşleşmesi: {string.Join(", ", matchingTags)}");
            }

            if (ability.CurrentLevel >= task.MinimumLevel)
            {
                reasons.Add($"Yeterli seviye: {ability.CurrentLevel}/{task.MinimumLevel}");
            }

            return string.Join("; ", reasons);
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Ability>> GetAbilitiesByComplexityAsync(AbilityComplexity complexity)
        {
            try
            {
                lock (_syncLock)
                {
                    return _abilities.Values;
                        .Where(a => a.Complexity == complexity && a.IsActive)
                        .Select(a => a.Clone())
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get abilities by complexity: {Complexity}", complexity);
                throw new AbilityStoreException(
                    $"Failed to retrieve abilities with complexity: {complexity}",
                    ex,
                    ErrorCodes.AbilityStore.RetrievalFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<IEnumerable<Ability>> GetPrerequisiteAbilitiesAsync(Guid abilityId)
        {
            try
            {
                var ability = await GetAbilityByIdAsync(abilityId);

                if (ability.PrerequisiteIds?.Any() != true)
                {
                    return Enumerable.Empty<Ability>();
                }

                var prerequisites = new List<Ability>();
                foreach (var prereqId in ability.PrerequisiteIds)
                {
                    var prereq = await GetAbilityByIdAsync(prereqId);
                    prerequisites.Add(prereq);
                }

                return prerequisites;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to get prerequisite abilities for: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to retrieve prerequisites for ability: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.RetrievalFailed);
            }
        }

        /// <inheritdoc/>
        public async Task<AbilityStatus> CheckAbilityStatusAsync(Guid abilityId)
        {
            try
            {
                var ability = await GetAbilityByIdAsync(abilityId);

                return new AbilityStatus;
                {
                    AbilityId = ability.Id,
                    AbilityName = ability.Name,
                    IsActive = ability.IsActive,
                    CurrentLevel = ability.CurrentLevel,
                    MaxLevel = ability.MaxLevel,
                    UsageCount = ability.UsageCount,
                    LastUsed = ability.LastUsed,
                    HealthScore = CalculateHealthScore(ability),
                    ReadyForUse = ability.IsActive && ability.CurrentLevel > 0,
                    MaintenanceRequired = ability.UsageCount > 1000 && ability.LastUsed < DateTime.UtcNow.AddDays(-30)
                };
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to check ability status: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to check ability status: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.StatusCheckFailed);
            }
        }

        private float CalculateHealthScore(Ability ability)
        {
            float score = 1.0f;

            // Son kullanım tarihi;
            var daysSinceLastUse = (DateTime.UtcNow - ability.LastUsed).TotalDays;
            if (daysSinceLastUse > 30)
            {
                score *= 0.7f;
            }
            else if (daysSinceLastUse > 90)
            {
                score *= 0.5f;
            }

            // Kullanım sayısı (aşırı kullanım)
            if (ability.UsageCount > 5000)
            {
                score *= 0.8f;
            }

            return score;
        }

        /// <inheritdoc/>
        public async Task<Ability> UpdateLearningProgressAsync(Guid abilityId, LearningProgress progress)
        {
            try
            {
                lock (_syncLock)
                {
                    if (!_abilities.TryGetValue(abilityId, out var ability))
                    {
                        throw new AbilityNotFoundException(
                            $"Ability with ID '{abilityId}' not found",
                            ErrorCodes.AbilityStore.AbilityNotFound);
                    }

                    ability.LearningProgress = progress;

                    // İlerlemeye göre seviye güncelle;
                    if (progress.CompletionPercentage >= 100 && ability.CurrentLevel < ability.MaxLevel)
                    {
                        ability.CurrentLevel = Math.Min(ability.CurrentLevel + 1, ability.MaxLevel);
                        progress.CompletionPercentage = 0; // Yeni seviye için sıfırla;
                    }

                    ability.UpdatedAt = DateTime.UtcNow;
                    _abilities[abilityId] = ability;

                    return ability.Clone();
                }
            }
            catch (Exception ex) when (!(ex is AbilityNotFoundException))
            {
                _logger.Error(ex, "Failed to update learning progress for ability: {AbilityId}", abilityId);
                throw new AbilityStoreException(
                    $"Failed to update learning progress: {abilityId}",
                    ex,
                    ErrorCodes.AbilityStore.UpdateFailed);
            }
        }

        private void ValidateAbility(Ability ability)
        {
            if (ability == null)
            {
                throw new ArgumentNullException(nameof(ability));
            }

            if (string.IsNullOrWhiteSpace(ability.Name))
            {
                throw new AbilityValidationException(
                    "Ability name cannot be empty",
                    ErrorCodes.AbilityStore.ValidationError);
            }

            if (string.IsNullOrWhiteSpace(ability.DisplayName))
            {
                throw new AbilityValidationException(
                    "Ability display name cannot be empty",
                    ErrorCodes.AbilityStore.ValidationError);
            }

            if (ability.CurrentLevel < 0 || ability.CurrentLevel > ability.MaxLevel)
            {
                throw new AbilityValidationException(
                    $"Current level must be between 0 and {ability.MaxLevel}",
                    ErrorCodes.AbilityStore.ValidationError);
            }

            if (ability.MaxLevel <= 0)
            {
                throw new AbilityValidationException(
                    "Max level must be greater than 0",
                    ErrorCodes.AbilityStore.ValidationError);
            }
        }

        /// <summary>
        /// Depoyu temizler ve kaynakları serbest bırakır.
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
                    lock (_syncLock)
                    {
                        _abilities.Clear();
                        _abilityNameIndex.Clear();
                    }
                }
                _disposed = true;
            }
        }

        ~AbilityStore()
        {
            Dispose(false);
        }
    }

    #region Data Models;

    /// <summary>
    /// Yetenek modeli.
    /// </summary>
    public class Ability : ICloneable;
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public AbilityCategory Category { get; set; }
        public AbilityComplexity Complexity { get; set; }
        public int BaseLevel { get; set; }
        public int CurrentLevel { get; set; }
        public int MaxLevel { get; set; }
        public int UsageCount { get; set; }
        public DateTime LastUsed { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsActive { get; set; }
        public List<Guid> PrerequisiteIds { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
        public LearningProgress LearningProgress { get; set; }
        public List<UsageRecord> UsageHistory { get; set; }

        public Ability Clone()
        {
            return new Ability;
            {
                Id = Id,
                Name = Name,
                DisplayName = DisplayName,
                Description = Description,
                Category = Category,
                Complexity = Complexity,
                BaseLevel = BaseLevel,
                CurrentLevel = CurrentLevel,
                MaxLevel = MaxLevel,
                UsageCount = UsageCount,
                LastUsed = LastUsed,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                IsActive = IsActive,
                PrerequisiteIds = PrerequisiteIds?.ToList() ?? new List<Guid>(),
                Tags = Tags?.ToList() ?? new List<string>(),
                Metadata = Metadata?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, object>(),
                LearningProgress = LearningProgress?.Clone(),
                UsageHistory = UsageHistory?.Select(r => r.Clone()).ToList() ?? new List<UsageRecord>()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Yetenek kategorileri.
    /// </summary>
    public enum AbilityCategory;
    {
        Language,
        Development,
        Cognitive,
        Analytical,
        Creative,
        Technical,
        Social,
        Administrative,
        Security,
        Research,
        Other;
    }

    /// <summary>
    /// Yetenek karmaşıklık seviyeleri.
    /// </summary>
    public enum AbilityComplexity;
    {
        Basic,
        Intermediate,
        Advanced,
        Expert,
        Master;
    }

    /// <summary>
    /// Öğrenme ilerlemesi.
    /// </summary>
    public class LearningProgress : ICloneable;
    {
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public float CompletionPercentage { get; set; }
        public DateTime LastPracticed { get; set; }
        public List<string> CompletedModules { get; set; }
        public Dictionary<string, float> SkillMetrics { get; set; }

        public LearningProgress Clone()
        {
            return new LearningProgress;
            {
                CurrentStep = CurrentStep,
                TotalSteps = TotalSteps,
                CompletionPercentage = CompletionPercentage,
                LastPracticed = LastPracticed,
                CompletedModules = CompletedModules?.ToList() ?? new List<string>(),
                SkillMetrics = SkillMetrics?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, float>()
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Kullanım bağlamı.
    /// </summary>
    public class UsageContext;
    {
        public string TaskType { get; set; }
        public DateTime Timestamp { get; set; }
        public string UserId { get; set; }
        public string SessionId { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public float SuccessRate { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    /// <summary>
    /// Kullanım kaydı.
    /// </summary>
    public class UsageRecord : ICloneable;
    {
        public DateTime Timestamp { get; set; }
        public UsageContext Context { get; set; }
        public float SuccessRate { get; set; }
        public string Notes { get; set; }

        public UsageRecord Clone()
        {
            return new UsageRecord;
            {
                Timestamp = Timestamp,
                Context = Context,
                SuccessRate = SuccessRate,
                Notes = Notes;
            };
        }

        object ICloneable.Clone() => Clone();
    }

    /// <summary>
    /// Görev açıklaması.
    /// </summary>
    public class TaskDescription;
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public AbilityComplexity RequiredComplexity { get; set; }
        public int MinimumLevel { get; set; }
        public List<string> Tags { get; set; }
        public Dictionary<string, object> Requirements { get; set; }
    }

    /// <summary>
    /// Yetenek önerisi.
    /// </summary>
    public class AbilityRecommendation;
    {
        public Ability Ability { get; set; }
        public float RelevanceScore { get; set; }
        public float Confidence { get; set; }
        public string Reasoning { get; set; }
    }

    /// <summary>
    /// Yetenek durumu.
    /// </summary>
    public class AbilityStatus;
    {
        public Guid AbilityId { get; set; }
        public string AbilityName { get; set; }
        public bool IsActive { get; set; }
        public int CurrentLevel { get; set; }
        public int MaxLevel { get; set; }
        public int UsageCount { get; set; }
        public DateTime LastUsed { get; set; }
        public float HealthScore { get; set; }
        public bool ReadyForUse { get; set; }
        public bool MaintenanceRequired { get; set; }
        public DateTime CheckedAt { get; set; } = DateTime.UtcNow;
    }

    #endregion;

    #region Events;

    /// <summary>
    /// Yetenek deposu başlatıldı olayı.
    /// </summary>
    public class AbilityStoreInitializedEvent : IEvent;
    {
        public DateTime Timestamp { get; set; }
        public int AbilityCount { get; set; }
        public string EventType => "AbilityStore.Initialized";
    }

    /// <summary>
    /// Yeni yetenek eklendi olayı.
    /// </summary>
    public class AbilityAddedEvent : IEvent;
    {
        public Guid AbilityId { get; set; }
        public string AbilityName { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType => "AbilityStore.AbilityAdded";
    }

    /// <summary>
    /// Yetenek güncellendi olayı.
    /// </summary>
    public class AbilityUpdatedEvent : IEvent;
    {
        public Guid AbilityId { get; set; }
        public string AbilityName { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType => "AbilityStore.AbilityUpdated";
    }

    /// <summary>
    /// Yetenek silindi olayı.
    /// </summary>
    public class AbilityRemovedEvent : IEvent;
    {
        public Guid AbilityId { get; set; }
        public string AbilityName { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType => "AbilityStore.AbilityRemoved";
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Yetenek deposu istisnası.
    /// </summary>
    public class AbilityStoreException : Exception
    {
        public string ErrorCode { get; }

        public AbilityStoreException(string message, string errorCode)
            : base(message)
        {
            ErrorCode = errorCode;
        }

        public AbilityStoreException(string message, Exception innerException, string errorCode)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
        }
    }

    /// <summary>
    /// Yetenek bulunamadı istisnası.
    /// </summary>
    public class AbilityNotFoundException : AbilityStoreException;
    {
        public AbilityNotFoundException(string message, string errorCode)
            : base(message, errorCode)
        {
        }
    }

    /// <summary>
    /// Yetenek doğrulama istisnası.
    /// </summary>
    public class AbilityValidationException : AbilityStoreException;
    {
        public AbilityValidationException(string message, string errorCode)
            : base(message, errorCode)
        {
        }
    }

    /// <summary>
    /// Yetenek deposu başlatma istisnası.
    /// </summary>
    public class AbilityStoreInitializationException : AbilityStoreException;
    {
        public AbilityStoreInitializationException(string message, Exception innerException, string errorCode)
            : base(message, innerException, errorCode)
        {
        }
    }

    #endregion;

    #region Error Codes;

    /// <summary>
    /// Yetenek deposu hata kodları.
    /// </summary>
    public static class AbilityStoreErrorCodes;
    {
        public const string InitializationFailed = "ABILITY_STORE_001";
        public const string AddFailed = "ABILITY_STORE_002";
        public const string UpdateFailed = "ABILITY_STORE_003";
        public const string RemoveFailed = "ABILITY_STORE_004";
        public const string RetrievalFailed = "ABILITY_STORE_005";
        public const string StatusCheckFailed = "ABILITY_STORE_006";
        public const string RecommendationFailed = "ABILITY_STORE_007";
        public const string DuplicateAbility = "ABILITY_STORE_008";
        public const string AbilityNotFound = "ABILITY_STORE_009";
        public const string ValidationError = "ABILITY_STORE_010";
        public const string MissingPrerequisites = "ABILITY_STORE_011";
        public const string HasDependents = "ABILITY_STORE_012";
    }

    #endregion;
}
