using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.MemorySystem;
using NEDA.Services.Messaging.EventBus;

namespace NEDA.Brain.KnowledgeBase.SkillRepository;
{
    /// <summary>
    /// Yetkinlik seviyelerini tanımlayan enum;
    /// </summary>
    public enum CompetencyLevel;
    {
        Novice = 1,        // Başlangıç seviyesi;
        Intermediate = 2,  // Orta seviye;
        Advanced = 3,      // İleri seviye;
        Expert = 4,        // Uzman seviyesi;
        Master = 5         // Usta seviyesi;
    }

    /// <summary>
    /// Yetkinlik gelişim durumunu tanımlayan enum;
    /// </summary>
    public enum DevelopmentStatus;
    {
        NotStarted = 0,    // Henüz başlamadı;
        InProgress = 1,    // Gelişim devam ediyor;
        Paused = 2,        // Geçici olarak duraklatıldı;
        Completed = 3,     // Tamamlandı;
        Mastered = 4       // Uzmanlaşıldı;
    }

    /// <summary>
    /// Yetkinlik kategorilerini tanımlayan sınıf;
    /// </summary>
    public static class CompetencyCategory;
    {
        public const string Technical = "Technical";
        public const string SoftSkills = "SoftSkills";
        public const string Leadership = "Leadership";
        public const string Creative = "Creative";
        public const string Analytical = "Analytical";
        public const string Communication = "Communication";
        public const string Management = "Management";
    }

    /// <summary>
    /// Bir yetkinliği temsil eden model;
    /// </summary>
    public class Competency;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public CompetencyLevel CurrentLevel { get; set; }
        public CompetencyLevel TargetLevel { get; set; }
        public DevelopmentStatus Status { get; set; }
        public double ProgressPercentage { get; set; }
        public int ExperiencePoints { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
        public DateTime LastPracticed { get; set; } = DateTime.UtcNow;
        public DateTime? TargetCompletionDate { get; set; }
        public List<string> PrerequisiteIds { get; set; } = new List<string>();
        public List<string> RelatedSkillIds { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        // Öğrenme metrikleri;
        public int PracticeCount { get; set; }
        public double SuccessRate { get; set; }
        public double ConfidenceScore { get; set; }
        public double ComplexityRating { get; set; }

        public bool IsPrerequisitesMet(List<Competency> allCompetencies)
        {
            if (!PrerequisiteIds.Any()) return true;

            var prerequisites = allCompetencies;
                .Where(c => PrerequisiteIds.Contains(c.Id))
                .ToList();

            return prerequisites.All(p =>
                p.Status >= DevelopmentStatus.Completed &&
                p.CurrentLevel >= CompetencyLevel.Intermediate);
        }

        public void UpdateProgress(int additionalPoints, bool success)
        {
            ExperiencePoints += additionalPoints;
            PracticeCount++;

            if (success)
            {
                var oldSuccessRate = SuccessRate;
                SuccessRate = ((oldSuccessRate * (PracticeCount - 1)) + 1) / PracticeCount;
            }
            else;
            {
                SuccessRate = (SuccessRate * (PracticeCount - 1)) / PracticeCount;
            }

            LastPracticed = DateTime.UtcNow;

            // Güven skorunu güncelle;
            ConfidenceScore = CalculateConfidenceScore();

            // İlerleme yüzdesini güncelle;
            UpdateProgressPercentage();
        }

        private double CalculateConfidenceScore()
        {
            var practiceFactor = Math.Min(PracticeCount / 10.0, 1.0); // Max 10 practice;
            var successFactor = SuccessRate;
            var experienceFactor = Math.Min(ExperiencePoints / 1000.0, 1.0); // Max 1000 XP;

            return (practiceFactor * 0.3) + (successFactor * 0.4) + (experienceFactor * 0.3);
        }

        private void UpdateProgressPercentage()
        {
            if (TargetLevel == CurrentLevel)
            {
                ProgressPercentage = 100;
                return;
            }

            var totalLevels = Enum.GetValues(typeof(CompetencyLevel)).Length;
            var currentLevelValue = (int)CurrentLevel;
            var targetLevelValue = (int)TargetLevel;

            // Seviyeler arası ilerleme;
            var levelProgress = ((double)(currentLevelValue - 1) / (targetLevelValue - 1)) * 100;

            // Deneyim puanlarına göre ilerleme;
            var requiredXP = targetLevelValue * 250; // Her seviye için 250 XP;
            var xpProgress = Math.Min((ExperiencePoints / requiredXP) * 100, 100);

            // Güven skoruna göre ilerleme;
            var confidenceProgress = ConfidenceScore * 100;

            // Ortalama ilerleme;
            ProgressPercentage = (levelProgress * 0.4) + (xpProgress * 0.4) + (confidenceProgress * 0.2);
        }

        public bool ShouldLevelUp()
        {
            var levelUpThresholds = new Dictionary<CompetencyLevel, int>
            {
                { CompetencyLevel.Novice, 100 },
                { CompetencyLevel.Intermediate, 300 },
                { CompetencyLevel.Advanced, 600 },
                { CompetencyLevel.Expert, 1000 }
            };

            if (levelUpThresholds.TryGetValue(CurrentLevel, out var threshold))
            {
                return ExperiencePoints >= threshold &&
                       SuccessRate >= 0.7 &&
                       ConfidenceScore >= 0.75;
            }

            return false;
        }

        public void LevelUp()
        {
            if (!ShouldLevelUp() || CurrentLevel == CompetencyLevel.Master)
                return;

            CurrentLevel = (CompetencyLevel)((int)CurrentLevel + 1);

            if (CurrentLevel >= TargetLevel)
            {
                Status = DevelopmentStatus.Completed;
                if (CurrentLevel == CompetencyLevel.Master)
                {
                    Status = DevelopmentStatus.Mastered;
                }
            }

            UpdateProgressPercentage();
        }
    }

    /// <summary>
    /// Yetkinlik öğrenme hedefini temsil eden model;
    /// </summary>
    public class LearningGoal;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string CompetencyId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime TargetDate { get; set; }
        public double Priority { get; set; } // 1-10 arası;
        public List<string> ActionItems { get; set; } = new List<string>();
        public Dictionary<string, double> SuccessCriteria { get; set; } = new Dictionary<string, double>();
        public bool IsCompleted { get; set; }
        public DateTime? CompletedDate { get; set; }
        public double CompletionPercentage { get; set; }
    }

    /// <summary>
    /// Yetkinlik değerlendirme sonucunu temsil eden model;
    /// </summary>
    public class CompetencyAssessment;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string CompetencyId { get; set; } = string.Empty;
        public DateTime AssessmentDate { get; set; } = DateTime.UtcNow;
        public CompetencyLevel AssessedLevel { get; set; }
        public double Score { get; set; } // 0-100 arası;
        public string Assessor { get; set; } = "System";
        public Dictionary<string, double> DimensionScores { get; set; } = new Dictionary<string, double>();
        public string Feedback { get; set; } = string.Empty;
        public List<string> Strengths { get; set; } = new List<string>();
        public List<string> ImprovementAreas { get; set; } = new List<string>();
        public DateTime? NextAssessmentDate { get; set; }
    }

    /// <summary>
    /// Yetkinlik matrisini temsil eden model;
    /// </summary>
    public class CompetencyMatrix;
    {
        public string UserId { get; set; } = string.Empty;
        public Dictionary<string, Competency> Competencies { get; set; } = new Dictionary<string, Competency>();
        public Dictionary<string, List<LearningGoal>> LearningGoalsByCategory { get; set; } = new Dictionary<string, List<LearningGoal>>();
        public List<CompetencyAssessment> AssessmentHistory { get; set; } = new List<CompetencyAssessment>();

        public double GetOverallCompetencyScore()
        {
            if (!Competencies.Any()) return 0;

            var weightedScores = Competencies.Values;
                .Select(c => c.ConfidenceScore * GetCategoryWeight(c.Category))
                .ToList();

            return weightedScores.Average();
        }

        public Dictionary<string, double> GetCategoryScores()
        {
            var scores = new Dictionary<string, double>();

            var grouped = Competencies.Values;
                .GroupBy(c => c.Category)
                .ToList();

            foreach (var group in grouped)
            {
                if (group.Any())
                {
                    scores[group.Key] = group.Average(c => c.ConfidenceScore);
                }
            }

            return scores;
        }

        private double GetCategoryWeight(string category)
        {
            var weights = new Dictionary<string, double>
            {
                { CompetencyCategory.Technical, 1.2 },
                { CompetencyCategory.Leadership, 1.1 },
                { CompetencyCategory.Analytical, 1.0 },
                { CompetencyCategory.Communication, 0.9 },
                { CompetencyCategory.Creative, 0.8 },
                { CompetencyCategory.SoftSkills, 0.7 },
                { CompetencyCategory.Management, 1.0 }
            };

            return weights.TryGetValue(category, out var weight) ? weight : 1.0;
        }
    }

    /// <summary>
    /// Yetkinlik yönetim sistemi - Kullanıcı yetkinliklerini, becerilerini ve yeterliliklerini yönetir;
    /// </summary>
    public interface ICompetencyManager;
    {
        // Yetkinlik CRUD operasyonları;
        Task<Competency> CreateCompetencyAsync(Competency competency, string userId);
        Task<Competency> GetCompetencyAsync(string competencyId, string userId);
        Task<List<Competency>> GetAllCompetenciesAsync(string userId);
        Task<Competency> UpdateCompetencyAsync(Competency competency, string userId);
        Task<bool> DeleteCompetencyAsync(string competencyId, string userId);

        // Yetkinlik geliştirme operasyonları;
        Task<Competency> PracticeCompetencyAsync(string competencyId, string userId, int points, bool success);
        Task<Competency> AssessCompetencyAsync(string competencyId, string userId, CompetencyAssessment assessment);
        Task<List<Competency>> GetRecommendedCompetenciesAsync(string userId);

        // Öğrenme hedefleri;
        Task<LearningGoal> CreateLearningGoalAsync(LearningGoal goal, string userId);
        Task<List<LearningGoal>> GetLearningGoalsAsync(string userId, string category = null);
        Task<LearningGoal> UpdateLearningGoalProgressAsync(string goalId, string userId, double progress);

        // Raporlama ve analiz;
        Task<CompetencyMatrix> GetCompetencyMatrixAsync(string userId);
        Task<Dictionary<string, double>> GetCompetencyGapAnalysisAsync(string userId);
        Task<List<Competency>> GetDevelopmentPriorityListAsync(string userId);

        // Sistem operasyonları;
        Task InitializeUserCompetenciesAsync(string userId);
        Task SyncWithKnowledgeBaseAsync(string userId);
        Task BackupCompetencyDataAsync(string userId);
    }

    /// <summary>
    /// Yetkinlik yönetim sisteminin implementasyonu;
    /// </summary>
    public class CompetencyManager : ICompetencyManager, IDisposable;
    {
        private readonly ILogger<CompetencyManager> _logger;
        private readonly IKnowledgeStore _knowledgeStore;
        private readonly IEventBus _eventBus;
        private readonly Dictionary<string, CompetencyMatrix> _userMatrices;
        private readonly object _syncLock = new object();
        private bool _disposed;

        public CompetencyManager(
            ILogger<CompetencyManager> logger,
            IKnowledgeStore knowledgeStore,
            IEventBus eventBus)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _knowledgeStore = knowledgeStore ?? throw new ArgumentNullException(nameof(knowledgeStore));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _userMatrices = new Dictionary<string, CompetencyMatrix>();

            _logger.LogInformation("CompetencyManager initialized");
        }

        /// <summary>
        /// Yeni bir yetkinlik oluşturur;
        /// </summary>
        public async Task<Competency> CreateCompetencyAsync(Competency competency, string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Creating competency '{Name}' for user {UserId}", competency.Name, userId);

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    // Benzersiz ID kontrolü;
                    if (matrix.Competencies.ContainsKey(competency.Id))
                    {
                        throw new InvalidOperationException($"Competency with ID {competency.Id} already exists");
                    }

                    // Önkoşul kontrolü;
                    if (competency.PrerequisiteIds.Any())
                    {
                        var unmetPrerequisites = competency.PrerequisiteIds;
                            .Where(prereqId => !matrix.Competencies.ContainsKey(prereqId))
                            .ToList();

                        if (unmetPrerequisites.Any())
                        {
                            throw new InvalidOperationException(
                                $"Missing prerequisites: {string.Join(", ", unmetPrerequisites)}");
                        }
                    }

                    matrix.Competencies[competency.Id] = competency;
                    _logger.LogInformation("Competency '{Name}' created successfully for user {UserId}",
                        competency.Name, userId);
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new CompetencyCreatedEvent;
                {
                    UserId = userId,
                    CompetencyId = competency.Id,
                    CompetencyName = competency.Name,
                    Timestamp = DateTime.UtcNow;
                });

                return competency;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating competency for user {UserId}", userId);
                throw new CompetencyManagementException("Failed to create competency", ex);
            }
        }

        /// <summary>
        /// Belirli bir yetkinliği getirir;
        /// </summary>
        public async Task<Competency> GetCompetencyAsync(string competencyId, string userId)
        {
            ValidateUserId(userId);
            ValidateCompetencyId(competencyId);

            try
            {
                _logger.LogDebug("Getting competency {CompetencyId} for user {UserId}", competencyId, userId);

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        throw new KeyNotFoundException($"No competency matrix found for user {userId}");
                    }

                    if (!matrix.Competencies.TryGetValue(competencyId, out var competency))
                    {
                        throw new KeyNotFoundException($"Competency {competencyId} not found for user {userId}");
                    }

                    return competency;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting competency {CompetencyId} for user {UserId}",
                    competencyId, userId);
                throw;
            }
        }

        /// <summary>
        /// Kullanıcının tüm yetkinliklerini getirir;
        /// </summary>
        public async Task<List<Competency>> GetAllCompetenciesAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting all competencies for user {UserId}", userId);

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        return new List<Competency>();
                    }

                    return matrix.Competencies.Values.ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting all competencies for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Yetkinliği günceller;
        /// </summary>
        public async Task<Competency> UpdateCompetencyAsync(Competency competency, string userId)
        {
            ValidateUserId(userId);
            ValidateCompetency(competency);

            try
            {
                _logger.LogDebug("Updating competency {CompetencyId} for user {UserId}",
                    competency.Id, userId);

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    if (!matrix.Competencies.ContainsKey(competency.Id))
                    {
                        throw new KeyNotFoundException($"Competency {competency.Id} not found");
                    }

                    matrix.Competencies[competency.Id] = competency;
                    _logger.LogInformation("Competency {CompetencyId} updated successfully", competency.Id);
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new CompetencyUpdatedEvent;
                {
                    UserId = userId,
                    CompetencyId = competency.Id,
                    Timestamp = DateTime.UtcNow;
                });

                return competency;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating competency {CompetencyId} for user {UserId}",
                    competency.Id, userId);
                throw new CompetencyManagementException("Failed to update competency", ex);
            }
        }

        /// <summary>
        /// Yetkinliği siler;
        /// </summary>
        public async Task<bool> DeleteCompetencyAsync(string competencyId, string userId)
        {
            ValidateUserId(userId);
            ValidateCompetencyId(competencyId);

            try
            {
                _logger.LogDebug("Deleting competency {CompetencyId} for user {UserId}", competencyId, userId);

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        return false;
                    }

                    // Bağımlı yetkinlikleri kontrol et;
                    var dependentCompetencies = matrix.Competencies.Values;
                        .Where(c => c.PrerequisiteIds.Contains(competencyId))
                        .ToList();

                    if (dependentCompetencies.Any())
                    {
                        var dependentNames = dependentCompetencies.Select(c => c.Name);
                        throw new InvalidOperationException(
                            $"Cannot delete competency. It is a prerequisite for: {string.Join(", ", dependentNames)}");
                    }

                    var removed = matrix.Competencies.Remove(competencyId);

                    if (removed)
                    {
                        _logger.LogInformation("Competency {CompetencyId} deleted successfully", competencyId);

                        // İlgili öğrenme hedeflerini de sil;
                        foreach (var goals in matrix.LearningGoalsByCategory.Values)
                        {
                            goals.RemoveAll(g => g.CompetencyId == competencyId);
                        }
                    }

                    return removed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting competency {CompetencyId} for user {UserId}",
                    competencyId, userId);
                throw new CompetencyManagementException("Failed to delete competency", ex);
            }
        }

        /// <summary>
        /// Yetkinlik üzerinde pratik yaparak deneyim kazanır;
        /// </summary>
        public async Task<Competency> PracticeCompetencyAsync(string competencyId, string userId, int points, bool success)
        {
            ValidateUserId(userId);
            ValidateCompetencyId(competencyId);

            if (points <= 0)
            {
                throw new ArgumentException("Points must be greater than zero", nameof(points));
            }

            try
            {
                _logger.LogDebug("Practicing competency {CompetencyId} for user {UserId} with {Points} points",
                    competencyId, userId, points);

                Competency updatedCompetency;

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    if (!matrix.Competencies.TryGetValue(competencyId, out var competency))
                    {
                        throw new KeyNotFoundException($"Competency {competencyId} not found");
                    }

                    // Pratik yap;
                    competency.UpdateProgress(points, success);

                    // Seviye atlama kontrolü;
                    if (competency.ShouldLevelUp())
                    {
                        competency.LevelUp();
                        _logger.LogInformation("Competency {CompetencyId} leveled up to {Level}",
                            competencyId, competency.CurrentLevel);
                    }

                    updatedCompetency = competency;
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new CompetencyPracticedEvent;
                {
                    UserId = userId,
                    CompetencyId = competencyId,
                    PointsEarned = points,
                    Success = success,
                    NewLevel = updatedCompetency.CurrentLevel,
                    Timestamp = DateTime.UtcNow;
                });

                // Bilgi tabanına senkronize et;
                await SyncCompetencyWithKnowledgeBaseAsync(updatedCompetency, userId);

                return updatedCompetency;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error practicing competency {CompetencyId} for user {UserId}",
                    competencyId, userId);
                throw new CompetencyManagementException("Failed to practice competency", ex);
            }
        }

        /// <summary>
        /// Yetkinliği değerlendirir;
        /// </summary>
        public async Task<Competency> AssessCompetencyAsync(string competencyId, string userId, CompetencyAssessment assessment)
        {
            ValidateUserId(userId);
            ValidateCompetencyId(competencyId);

            if (assessment == null)
            {
                throw new ArgumentNullException(nameof(assessment));
            }

            try
            {
                _logger.LogDebug("Assessing competency {CompetencyId} for user {UserId}", competencyId, userId);

                Competency updatedCompetency;

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    if (!matrix.Competencies.TryGetValue(competencyId, out var competency))
                    {
                        throw new KeyNotFoundException($"Competency {competencyId} not found");
                    }

                    // Değerlendirme geçmişine ekle;
                    matrix.AssessmentHistory.Add(assessment);

                    // Yetkinlik seviyesini güncelle (eğer değerlendirme seviyesi daha yüksekse)
                    if (assessment.AssessedLevel > competency.CurrentLevel)
                    {
                        competency.CurrentLevel = assessment.AssessedLevel;
                        competency.ConfidenceScore = assessment.Score / 100.0;

                        if (competency.CurrentLevel >= competency.TargetLevel)
                        {
                            competency.Status = DevelopmentStatus.Completed;
                        }

                        competency.UpdateProgressPercentage();

                        _logger.LogInformation("Competency {CompetencyId} level updated to {Level} based on assessment",
                            competencyId, competency.CurrentLevel);
                    }

                    updatedCompetency = competency;
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new CompetencyAssessedEvent;
                {
                    UserId = userId,
                    CompetencyId = competencyId,
                    AssessmentId = assessment.Id,
                    AssessedLevel = assessment.AssessedLevel,
                    Score = assessment.Score,
                    Timestamp = DateTime.UtcNow;
                });

                return updatedCompetency;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assessing competency {CompetencyId} for user {UserId}",
                    competencyId, userId);
                throw new CompetencyManagementException("Failed to assess competency", ex);
            }
        }

        /// <summary>
        /// Kullanıcı için önerilen yetkinlikleri getirir;
        /// </summary>
        public async Task<List<Competency>> GetRecommendedCompetenciesAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting recommended competencies for user {UserId}", userId);

                List<Competency> recommendations = new List<Competency>();

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        return recommendations;
                    }

                    var userCompetencies = matrix.Competencies.Values.ToList();

                    // 1. Önkoşulları karşılanan yetkinlikleri öner;
                    var availableCompetencies = userCompetencies;
                        .Where(c => c.IsPrerequisitesMet(userCompetencies) &&
                                   c.Status != DevelopmentStatus.Completed &&
                                   c.Status != DevelopmentStatus.Mastered)
                        .OrderByDescending(c => c.Priority)
                        .Take(5)
                        .ToList();

                    recommendations.AddRange(availableCompetencies);

                    // 2. İlgili becerileri öner;
                    var currentCompetencies = userCompetencies;
                        .Where(c => c.Status == DevelopmentStatus.InProgress ||
                                   c.CurrentLevel >= CompetencyLevel.Intermediate)
                        .ToList();

                    foreach (var competency in currentCompetencies)
                    {
                        var relatedCompetencies = userCompetencies;
                            .Where(c => competency.RelatedSkillIds.Contains(c.Id) &&
                                       !recommendations.Contains(c))
                            .Take(2)
                            .ToList();

                        recommendations.AddRange(relatedCompetencies);
                    }

                    // 3. Kategori dengelenmesi için öneriler;
                    var categoryScores = matrix.GetCategoryScores();
                    var weakCategories = categoryScores;
                        .Where(kv => kv.Value < 0.5)
                        .Select(kv => kv.Key)
                        .ToList();

                    foreach (var weakCategory in weakCategories)
                    {
                        var categoryCompetencies = userCompetencies;
                            .Where(c => c.Category == weakCategory &&
                                       c.Status != DevelopmentStatus.Completed)
                            .OrderBy(c => c.ProgressPercentage)
                            .Take(2)
                            .ToList();

                        recommendations.AddRange(categoryCompetencies);
                    }
                }

                // Benzersiz öneriler;
                recommendations = recommendations;
                    .DistinctBy(c => c.Id)
                    .Take(10)
                    .ToList();

                return recommendations;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting recommended competencies for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Yeni bir öğrenme hedefi oluşturur;
        /// </summary>
        public async Task<LearningGoal> CreateLearningGoalAsync(LearningGoal goal, string userId)
        {
            ValidateUserId(userId);

            if (goal == null)
            {
                throw new ArgumentNullException(nameof(goal));
            }

            try
            {
                _logger.LogDebug("Creating learning goal for competency {CompetencyId} and user {UserId}",
                    goal.CompetencyId, userId);

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    // Yetkinlik kontrolü;
                    if (!matrix.Competencies.TryGetValue(goal.CompetencyId, out var competency))
                    {
                        throw new KeyNotFoundException($"Competency {goal.CompetencyId} not found");
                    }

                    // Kategoriye göre grupla;
                    if (!matrix.LearningGoalsByCategory.ContainsKey(competency.Category))
                    {
                        matrix.LearningGoalsByCategory[competency.Category] = new List<LearningGoal>();
                    }

                    matrix.LearningGoalsByCategory[competency.Category].Add(goal);
                    _logger.LogInformation("Learning goal created for competency {CompetencyId}", goal.CompetencyId);
                }

                return goal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating learning goal for user {UserId}", userId);
                throw new CompetencyManagementException("Failed to create learning goal", ex);
            }
        }

        /// <summary>
        /// Öğrenme hedeflerini getirir;
        /// </summary>
        public async Task<List<LearningGoal>> GetLearningGoalsAsync(string userId, string category = null)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting learning goals for user {UserId}, category: {Category}",
                    userId, category ?? "All");

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        return new List<LearningGoal>();
                    }

                    if (string.IsNullOrEmpty(category))
                    {
                        return matrix.LearningGoalsByCategory;
                            .SelectMany(kv => kv.Value)
                            .ToList();
                    }
                    else;
                    {
                        return matrix.LearningGoalsByCategory;
                            .TryGetValue(category, out var goals) ? goals : new List<LearningGoal>();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting learning goals for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Öğrenme hedefi ilerlemesini günceller;
        /// </summary>
        public async Task<LearningGoal> UpdateLearningGoalProgressAsync(string goalId, string userId, double progress)
        {
            ValidateUserId(userId);

            if (string.IsNullOrEmpty(goalId))
            {
                throw new ArgumentException("Goal ID cannot be null or empty", nameof(goalId));
            }

            if (progress < 0 || progress > 100)
            {
                throw new ArgumentException("Progress must be between 0 and 100", nameof(progress));
            }

            try
            {
                _logger.LogDebug("Updating learning goal {GoalId} progress to {Progress}% for user {UserId}",
                    goalId, progress, userId);

                LearningGoal updatedGoal = null;

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        throw new KeyNotFoundException($"No competency matrix found for user {userId}");
                    }

                    foreach (var goals in matrix.LearningGoalsByCategory.Values)
                    {
                        var goal = goals.FirstOrDefault(g => g.Id == goalId);
                        if (goal != null)
                        {
                            goal.CompletionPercentage = progress;

                            if (progress >= 100)
                            {
                                goal.IsCompleted = true;
                                goal.CompletedDate = DateTime.UtcNow;

                                // İlgili yetkinliğe deneyim puanı ekle;
                                if (matrix.Competencies.TryGetValue(goal.CompetencyId, out var competency))
                                {
                                    competency.UpdateProgress(50, true); // Hedef tamamlandığında 50 XP;
                                }
                            }

                            updatedGoal = goal;
                            break;
                        }
                    }

                    if (updatedGoal == null)
                    {
                        throw new KeyNotFoundException($"Learning goal {goalId} not found");
                    }
                }

                return updatedGoal;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating learning goal progress for user {UserId}", userId);
                throw new CompetencyManagementException("Failed to update learning goal progress", ex);
            }
        }

        /// <summary>
        /// Kullanıcının yetkinlik matrisini getirir;
        /// </summary>
        public async Task<CompetencyMatrix> GetCompetencyMatrixAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting competency matrix for user {UserId}", userId);

                lock (_syncLock)
                {
                    return GetOrCreateUserMatrix(userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting competency matrix for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Yetkinlik açığı analizi yapar;
        /// </summary>
        public async Task<Dictionary<string, double>> GetCompetencyGapAnalysisAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Performing competency gap analysis for user {UserId}", userId);

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);
                    var gapAnalysis = new Dictionary<string, double>();

                    foreach (var competency in matrix.Competencies.Values)
                    {
                        var targetLevelValue = (int)competency.TargetLevel;
                        var currentLevelValue = (int)competency.CurrentLevel;

                        var levelGap = targetLevelValue - currentLevelValue;
                        var progressGap = 100 - competency.ProgressPercentage;

                        // Açık puanı (0-1 arası, 1 maksimum açık)
                        var gapScore = (levelGap * 0.6) + (progressGap / 100 * 0.4);

                        gapAnalysis[competency.Name] = Math.Min(gapScore, 1.0);
                    }

                    return gapAnalysis;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error performing competency gap analysis for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Geliştirme önceliği listesini getirir;
        /// </summary>
        public async Task<List<Competency>> GetDevelopmentPriorityListAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogDebug("Getting development priority list for user {UserId}", userId);

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    var priorityList = matrix.Competencies.Values;
                        .Where(c => c.Status != DevelopmentStatus.Completed &&
                                   c.Status != DevelopmentStatus.Mastered)
                        .Select(c => new;
                        {
                            Competency = c,
                            PriorityScore = CalculatePriorityScore(c, matrix)
                        })
                        .OrderByDescending(x => x.PriorityScore)
                        .Select(x => x.Competency)
                        .Take(10)
                        .ToList();

                    return priorityList;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting development priority list for user {UserId}", userId);
                throw;
            }
        }

        /// <summary>
        /// Kullanıcı için varsayılan yetkinlikleri başlatır;
        /// </summary>
        public async Task InitializeUserCompetenciesAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogInformation("Initializing default competencies for user {UserId}", userId);

                var defaultCompetencies = new List<Competency>
                {
                    new Competency;
                    {
                        Name = "Problem Solving",
                        Description = "Ability to identify, analyze, and solve problems effectively",
                        Category = CompetencyCategory.Analytical,
                        CurrentLevel = CompetencyLevel.Novice,
                        TargetLevel = CompetencyLevel.Advanced,
                        Status = DevelopmentStatus.InProgress,
                        ComplexityRating = 0.7;
                    },
                    new Competency;
                    {
                        Name = "Communication",
                        Description = "Ability to convey information clearly and effectively",
                        Category = CompetencyCategory.Communication,
                        CurrentLevel = CompetencyLevel.Novice,
                        TargetLevel = CompetencyLevel.Intermediate,
                        Status = DevelopmentStatus.InProgress,
                        ComplexityRating = 0.5;
                    },
                    new Competency;
                    {
                        Name = "Technical Proficiency",
                        Description = "Mastery of technical tools and methodologies",
                        Category = CompetencyCategory.Technical,
                        CurrentLevel = CompetencyLevel.Novice,
                        TargetLevel = CompetencyLevel.Expert,
                        Status = DevelopmentStatus.InProgress,
                        ComplexityRating = 0.8;
                    },
                    new Competency;
                    {
                        Name = "Creative Thinking",
                        Description = "Ability to generate innovative ideas and solutions",
                        Category = CompetencyCategory.Creative,
                        CurrentLevel = CompetencyLevel.Novice,
                        TargetLevel = CompetencyLevel.Intermediate,
                        Status = DevelopmentStatus.InProgress,
                        ComplexityRating = 0.6;
                    }
                };

                lock (_syncLock)
                {
                    var matrix = GetOrCreateUserMatrix(userId);

                    foreach (var competency in defaultCompetencies)
                    {
                        if (!matrix.Competencies.ContainsKey(competency.Id))
                        {
                            matrix.Competencies[competency.Id] = competency;
                        }
                    }
                }

                _logger.LogInformation("Default competencies initialized for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing competencies for user {UserId}", userId);
                throw new CompetencyManagementException("Failed to initialize user competencies", ex);
            }
        }

        /// <summary>
        /// Bilgi tabanı ile senkronizasyon yapar;
        /// </summary>
        public async Task SyncWithKnowledgeBaseAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogInformation("Syncing competencies with knowledge base for user {UserId}", userId);

                List<Competency> competencies;

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out var matrix))
                    {
                        return;
                    }

                    competencies = matrix.Competencies.Values.ToList();
                }

                foreach (var competency in competencies)
                {
                    await SyncCompetencyWithKnowledgeBaseAsync(competency, userId);
                }

                _logger.LogInformation("Competencies synced with knowledge base for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing competencies with knowledge base for user {UserId}", userId);
                throw new CompetencyManagementException("Failed to sync with knowledge base", ex);
            }
        }

        /// <summary>
        /// Yetkinlik verilerini yedekler;
        /// </summary>
        public async Task BackupCompetencyDataAsync(string userId)
        {
            ValidateUserId(userId);

            try
            {
                _logger.LogInformation("Backing up competency data for user {UserId}", userId);

                CompetencyMatrix matrix;

                lock (_syncLock)
                {
                    if (!_userMatrices.TryGetValue(userId, out matrix))
                    {
                        return;
                    }
                }

                // JSON serileştirme (gerçek uygulamada dosya sistemi veya blob storage'a kaydedilir)
                var backupData = new;
                {
                    UserId = userId,
                    BackupTime = DateTime.UtcNow,
                    CompetencyCount = matrix.Competencies.Count,
                    OverallScore = matrix.GetOverallCompetencyScore(),
                    Competencies = matrix.Competencies.Values,
                    LearningGoals = matrix.LearningGoalsByCategory,
                    Assessments = matrix.AssessmentHistory;
                };

                // Olay yayınla;
                await _eventBus.PublishAsync(new CompetencyBackupEvent;
                {
                    UserId = userId,
                    BackupTime = DateTime.UtcNow,
                    CompetencyCount = matrix.Competencies.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Competency data backed up for user {UserId}", userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error backing up competency data for user {UserId}", userId);
                throw new CompetencyManagementException("Failed to backup competency data", ex);
            }
        }

        // Yardımcı metodlar;

        private CompetencyMatrix GetOrCreateUserMatrix(string userId)
        {
            if (!_userMatrices.TryGetValue(userId, out var matrix))
            {
                matrix = new CompetencyMatrix;
                {
                    UserId = userId,
                    Competencies = new Dictionary<string, Competency>(),
                    LearningGoalsByCategory = new Dictionary<string, List<LearningGoal>>(),
                    AssessmentHistory = new List<CompetencyAssessment>()
                };
                _userMatrices[userId] = matrix;
            }

            return matrix;
        }

        private async Task SyncCompetencyWithKnowledgeBaseAsync(Competency competency, string userId)
        {
            try
            {
                // Yetkinlik bilgilerini bilgi tabanına kaydet;
                var knowledgeFact = new;
                {
                    Type = "Competency",
                    UserId = userId,
                    CompetencyId = competency.Id,
                    CompetencyName = competency.Name,
                    CurrentLevel = competency.CurrentLevel.ToString(),
                    ConfidenceScore = competency.ConfidenceScore,
                    ExperiencePoints = competency.ExperiencePoints,
                    LastUpdated = DateTime.UtcNow;
                };

                // _knowledgeStore'a kaydetme işlemi;
                // Gerçek uygulamada: await _knowledgeStore.StoreFactAsync(knowledgeFact);

                _logger.LogDebug("Competency {CompetencyId} synced with knowledge base", competency.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync competency {CompetencyId} with knowledge base", competency.Id);
                // Sync hatası işlemi devam etmeli;
            }
        }

        private double CalculatePriorityScore(Competency competency, CompetencyMatrix matrix)
        {
            var gapScore = ((int)competency.TargetLevel - (int)competency.CurrentLevel) * 0.3;
            var progressScore = (100 - competency.ProgressPercentage) / 100 * 0.2;
            var timeScore = competency.TargetCompletionDate.HasValue ?
                (competency.TargetCompletionDate.Value - DateTime.UtcNow).TotalDays / 30 * 0.2 : 0.3;
            var categoryWeight = GetCategoryWeight(competency.Category) * 0.2;
            var dependencyScore = competency.PrerequisiteIds.Any() ? 0.1 : 0.2;

            return gapScore + progressScore + timeScore + categoryWeight + dependencyScore;
        }

        private double GetCategoryWeight(string category)
        {
            var weights = new Dictionary<string, double>
            {
                { CompetencyCategory.Technical, 1.2 },
                { CompetencyCategory.Leadership, 1.1 },
                { CompetencyCategory.Analytical, 1.0 },
                { CompetencyCategory.Communication, 0.9 },
                { CompetencyCategory.Creative, 0.8 },
                { CompetencyCategory.SoftSkills, 0.7 },
                { CompetencyCategory.Management, 1.0 }
            };

            return weights.TryGetValue(category, out var weight) ? weight : 1.0;
        }

        private void ValidateUserId(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));
            }
        }

        private void ValidateCompetencyId(string competencyId)
        {
            if (string.IsNullOrWhiteSpace(competencyId))
            {
                throw new ArgumentException("Competency ID cannot be null or empty", nameof(competencyId));
            }
        }

        private void ValidateCompetency(Competency competency)
        {
            if (competency == null)
            {
                throw new ArgumentNullException(nameof(competency));
            }

            if (string.IsNullOrWhiteSpace(competency.Name))
            {
                throw new ArgumentException("Competency name cannot be null or empty", nameof(competency.Name));
            }

            if (string.IsNullOrWhiteSpace(competency.Category))
            {
                throw new ArgumentException("Competency category cannot be null or empty", nameof(competency.Category));
            }
        }

        // Olay sınıfları;

        public class CompetencyCreatedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string CompetencyId { get; set; } = string.Empty;
            public string CompetencyName { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class CompetencyUpdatedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string CompetencyId { get; set; } = string.Empty;
            public DateTime Timestamp { get; set; }
        }

        public class CompetencyPracticedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string CompetencyId { get; set; } = string.Empty;
            public int PointsEarned { get; set; }
            public bool Success { get; set; }
            public CompetencyLevel NewLevel { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CompetencyAssessedEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public string CompetencyId { get; set; } = string.Empty;
            public string AssessmentId { get; set; } = string.Empty;
            public CompetencyLevel AssessedLevel { get; set; }
            public double Score { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CompetencyBackupEvent : IEvent;
        {
            public string UserId { get; set; } = string.Empty;
            public DateTime BackupTime { get; set; }
            public int CompetencyCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        // Özel exception sınıfı;

        public class CompetencyManagementException : Exception
        {
            public CompetencyManagementException() { }
            public CompetencyManagementException(string message) : base(message) { }
            public CompetencyManagementException(string message, Exception innerException)
                : base(message, innerException) { }
        }

        // IDisposable implementasyonu;

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
                    lock (_syncLock)
                    {
                        _userMatrices.Clear();
                    }

                    _logger.LogInformation("CompetencyManager disposed");
                }

                _disposed = true;
            }
        }

        ~CompetencyManager()
        {
            Dispose(false);
        }
    }
}
