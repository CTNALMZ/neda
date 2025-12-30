using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.API.ClientSDK;
using NEDA.Brain.KnowledgeBase;
using NEDA.Brain.MemorySystem;
using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace NEDA.Communication.EmotionalIntelligence.PersonalityTraits;
{
    /// <summary>
    /// Personality Dimension Enumeration (Big Five Model)
    /// </summary>
    public enum PersonalityDimension;
    {
        Openness = 0,        // Openness to experience;
        Conscientiousness = 1, // Organized and dependable;
        Extraversion = 2,    // Sociable and energetic;
        Agreeableness = 3,   // Compassionate and cooperative;
        Neuroticism = 4      // Emotional stability (inverse)
    }

    /// <summary>
    /// Communication Style;
    /// </summary>
    public enum CommunicationStyle;
    {
        Analytical = 0,      // Fact-based, logical;
        Intuitive = 1,       // Insight-based, creative;
        Functional = 2,      // Process-oriented, practical;
        Personal = 3         // Emotion-focused, empathetic;
    }

    /// <summary>
    /// Humor Style;
    /// </summary>
    public enum HumorStyle;
    {
        None = 0,
        Light = 1,           // Gentle, positive humor;
        Witty = 2,           // Clever, intellectual humor;
        Playful = 3,         // Fun, whimsical humor;
        Sarcastic = 4,       // Dry, ironic humor (use with caution)
        Adaptive = 5         // Matches user's style;
    }

    /// <summary>
    /// Character Archetype;
    /// </summary>
    public enum CharacterArchetype;
    {
        Mentor = 0,          // Wise, guiding;
        Companion = 1,       // Friendly, supportive;
        Expert = 2,          // Knowledgeable, precise;
        Creative = 3,        // Imaginative, innovative;
        Protector = 4,       // Caring, vigilant;
        Diplomat = 5,        // Tactful, balanced;
        Enthusiast = 6,      // Energetic, positive;
        Analyst = 7          // Logical, detail-oriented;
    }

    /// <summary>
    /// Character Configuration;
    /// </summary>
    public class CharacterConfig;
    {
        public string DefaultCharacterId { get; set; } = "default";
        public bool EnableDynamicAdaptation { get; set; } = true;
        public double AdaptationRate { get; set; } = 0.1;
        public int MinInteractionThreshold { get; set; } = 10;
        public TimeSpan ProfileCacheDuration { get; set; } = TimeSpan.FromHours(1);
        public Dictionary<string, CharacterProfile> PresetProfiles { get; set; }
        public PersonalityConstraints PersonalityConstraints { get; set; }
        public StyleConsistencyRules ConsistencyRules { get; set; }
    }

    /// <summary>
    /// Personality Constraints;
    /// </summary>
    public class PersonalityConstraints;
    {
        public double MinOpenness { get; set; } = 0.2;
        public double MaxOpenness { get; set; } = 0.9;
        public double MinConscientiousness { get; set; } = 0.3;
        public double MaxConscientiousness { get; set; } = 0.95;
        public double MinExtraversion { get; set; } = 0.1;
        public double MaxExtraversion { get; set; } = 0.8;
        public double MinAgreeableness { get; set; } = 0.4;
        public double MaxAgreeableness { get; set; } = 1.0;
        public double MinNeuroticism { get; set; } = 0.0;
        public double MaxNeuroticism { get; set; } = 0.5;
    }

    /// <summary>
    /// Style Consistency Rules;
    /// </summary>
    public class StyleConsistencyRules;
    {
        public double MaxStyleDrift { get; set; } = 0.3;
        public int ConsistencyWindow { get; set; } = 50; // interactions;
        public bool EnforceArchetypeConsistency { get; set; } = true;
        public Dictionary<CharacterArchetype, List<CommunicationStyle>> ArchetypeStyleMapping { get; set; }
    }

    /// <summary>
    /// Character Profile;
    /// </summary>
    public class CharacterProfile;
    {
        public string ProfileId { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime LastUpdated { get; set; }

        // Personality Traits (Big Five, normalized 0-1)
        public double Openness { get; set; } = 0.7;
        public double Conscientiousness { get; set; } = 0.8;
        public double Extraversion { get; set; } = 0.5;
        public double Agreeableness { get; set; } = 0.9;
        public double Neuroticism { get; set; } = 0.2; // Lower is better (emotional stability)

        // Communication Style Preferences;
        public Dictionary<CommunicationStyle, double> StylePreferences { get; set; }
        public HumorStyle PreferredHumorStyle { get; set; } = HumorStyle.Light;
        public double HumorFrequency { get; set; } = 0.2; // 0-1;

        // Archetype;
        public CharacterArchetype PrimaryArchetype { get; set; } = CharacterArchetype.Companion;
        public List<CharacterArchetype> SecondaryArchetypes { get; set; }

        // Behavioral Parameters;
        public double FormalityLevel { get; set; } = 0.5; // 0=casual, 1=formal;
        public double VerbosityLevel { get; set; } = 0.6; // 0=concise, 1=detailed;
        public double EmpathyLevel { get; set; } = 0.8; // 0=neutral, 1=highly empathetic;
        public double Assertiveness { get; set; } = 0.4; // 0=passive, 1=assertive;

        // Adaptation Data;
        public int TotalInteractions { get; set; }
        public Dictionary<string, int> InteractionHistory { get; set; } // UserId -> Count;
        public List<BehaviorSample> RecentBehaviors { get; set; }

        // Metadata;
        public Dictionary<string, object> Metadata { get; set; }

        public double GetPersonalityScore(PersonalityDimension dimension)
        {
            return dimension switch;
            {
                PersonalityDimension.Openness => Openness,
                PersonalityDimension.Conscientiousness => Conscientiousness,
                PersonalityDimension.Extraversion => Extraversion,
                PersonalityDimension.Agreeableness => Agreeableness,
                PersonalityDimension.Neuroticism => Neuroticism,
                _ => 0.5;
            };
        }

        public CommunicationStyle GetDominantStyle()
        {
            return StylePreferences?.OrderByDescending(kv => kv.Value).FirstOrDefault().Key;
                ?? CommunicationStyle.Personal;
        }
    }

    /// <summary>
    /// Behavior Sample;
    /// </summary>
    public class BehaviorSample;
    {
        public DateTime Timestamp { get; set; }
        public string InteractionId { get; set; }
        public string UserId { get; set; }
        public string Context { get; set; }
        public CommunicationStyle UsedStyle { get; set; }
        public double StyleIntensity { get; set; }
        public bool UsedHumor { get; set; }
        public HumorStyle HumorStyle { get; set; }
        public double UserResponseScore { get; set; } // -1 to 1;
        public Dictionary<string, double> BehavioralMetrics { get; set; }
    }

    /// <summary>
    /// Character Adaptation Request;
    /// </summary>
    public class CharacterAdaptationRequest;
    {
        public string ProfileId { get; set; }
        public string UserId { get; set; }
        public string Context { get; set; }
        public UserPersonality UserPersonality { get; set; }
        public ConversationContext ConversationContext { get; set; }
        public AdaptationGoal AdaptationGoal { get; set; }
        public Dictionary<string, object> Constraints { get; set; }
    }

    /// <summary>
    /// User Personality;
    /// </summary>
    public class UserPersonality;
    {
        public string UserId { get; set; }
        public double Openness { get; set; }
        public double Conscientiousness { get; set; }
        public double Extraversion { get; set; }
        public double Agreeableness { get; set; }
        public double Neuroticism { get; set; }
        public CommunicationStyle PreferredStyle { get; set; }
        public double FormalityPreference { get; set; }
    }

    /// <summary>
    /// Conversation Context;
    /// </summary>
    public class ConversationContext;
    {
        public string Topic { get; set; }
        public EmotionalContext EmotionalState { get; set; }
        public UrgencyLevel Urgency { get; set; }
        public FormalityLevel RequiredFormality { get; set; }
        public List<string> RecentTopics { get; set; }
        public Dictionary<string, object> ContextData { get; set; }
    }

    /// <summary>
    /// Adaptation Goal;
    /// </summary>
    public class AdaptationGoal;
    {
        public GoalType Type { get; set; }
        public double TargetValue { get; set; }
        public TimeSpan Timeframe { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
    }

    /// <summary>
    /// Goal Types;
    /// </summary>
    public enum GoalType;
    {
        IncreaseRapport = 0,
        ImproveEfficiency = 1,
        EnhanceTrust = 2,
        MatchUserStyle = 3,
        OptimizeEngagement = 4,
        MaintainConsistency = 5;
    }

    /// <summary>
    /// Character Builder Interface;
    /// </summary>
    public interface ICharacterBuilder;
    {
        Task<CharacterProfile> GetOrCreateProfileAsync(string profileId, string userId = null);
        Task<CharacterProfile> AdaptProfileAsync(CharacterAdaptationRequest request);
        Task<CharacterProfile> CloneProfileAsync(string sourceProfileId, string newProfileId, string newName = null);
        Task<bool> UpdateProfileAsync(CharacterProfile profile);
        Task<bool> DeleteProfileAsync(string profileId);

        Task<CommunicationStyle> DetermineOptimalStyleAsync(string profileId, ConversationContext context);
        Task<HumorStyle> DetermineHumorStyleAsync(string profileId, ConversationContext context);
        Task<double> CalculateFormalityLevelAsync(string profileId, ConversationContext context);

        Task<List<CharacterProfile>> SearchProfilesAsync(ProfileSearchCriteria criteria);
        Task<Dictionary<string, double>> AnalyzeProfileCompatibilityAsync(string profileId, UserPersonality userPersonality);
        Task<List<BehaviorSample>> GetBehaviorHistoryAsync(string profileId, DateTime? startDate = null, int limit = 100);

        Task TrainOnInteractionAsync(string profileId, BehaviorSample interaction);
        Task ResetProfileAdaptationAsync(string profileId);

        Task<CharacterProfile> GenerateCharacterFromTemplateAsync(string templateName, Dictionary<string, object> parameters);
        Task ValidateProfileConsistencyAsync(string profileId);
    }

    /// <summary>
    /// Profile Search Criteria;
    /// </summary>
    public class ProfileSearchCriteria;
    {
        public List<CharacterArchetype> Archetypes { get; set; }
        public Range<double>? OpennessRange { get; set; }
        public Range<double>? ExtraversionRange { get; set; }
        public Range<double>? AgreeablenessRange { get; set; }
        public CommunicationStyle? PreferredStyle { get; set; }
        public int MinInteractions { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public string NameContains { get; set; }
    }

    /// <summary>
    /// Range Structure;
    /// </summary>
    public struct Range<T> where T : IComparable<T>
    {
        public T Min { get; set; }
        public T Max { get; set; }

        public bool Contains(T value)
        {
            return value.CompareTo(Min) >= 0 && value.CompareTo(Max) <= 0;
        }
    }

    /// <summary>
    /// Character Builder Implementation;
    /// </summary>
    public class CharacterBuilder : ICharacterBuilder;
    {
        private readonly ILogger<CharacterBuilder> _logger;
        private readonly IMemoryCache _cache;
        private readonly ILongTermMemory _longTermMemory;
        private readonly IKnowledgeGraph _knowledgeGraph;
        private readonly IMetricsCollector _metricsCollector;
        private readonly IEventBus _eventBus;
        private readonly CharacterConfig _config;
        private readonly ISettingsManager _settingsManager;

        private readonly Dictionary<string, CharacterProfile> _activeProfiles;
        private readonly object _profileLock = new object();
        private readonly Random _random = new Random();

        private const string ProfileCachePrefix = "character_profile_";
        private const string BehaviorCachePrefix = "behavior_history_";
        private const string DefaultProfileKey = "default_character_profile";

        /// <summary>
        /// Constructor;
        /// </summary>
        public CharacterBuilder(
            ILogger<CharacterBuilder> logger,
            IMemoryCache cache,
            ILongTermMemory longTermMemory,
            IKnowledgeGraph knowledgeGraph,
            IMetricsCollector metricsCollector,
            IEventBus eventBus,
            IOptions<CharacterConfig> configOptions,
            ISettingsManager settingsManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _longTermMemory = longTermMemory ?? throw new ArgumentNullException(nameof(longTermMemory));
            _knowledgeGraph = knowledgeGraph ?? throw new ArgumentNullException(nameof(knowledgeGraph));
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _settingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
            _config = configOptions?.Value ?? CreateDefaultConfig();

            _activeProfiles = new Dictionary<string, CharacterProfile>();

            InitializePresetProfiles();
            LoadConfiguration();
            SubscribeToEvents();

            _logger.LogInformation("CharacterBuilder initialized with {ProfileCount} preset profiles",
                _config.PresetProfiles?.Count ?? 0);
        }

        /// <summary>
        /// Get or create character profile;
        /// </summary>
        public async Task<CharacterProfile> GetOrCreateProfileAsync(string profileId, string userId = null)
        {
            try
            {
                ValidateProfileId(profileId);

                // Check cache first;
                var cacheKey = $"{ProfileCachePrefix}{profileId}";
                if (_cache.TryGetValue(cacheKey, out CharacterProfile cachedProfile))
                {
                    _logger.LogDebug("Retrieved profile {ProfileId} from cache", profileId);
                    return cachedProfile;
                }

                // Check active profiles;
                lock (_profileLock)
                {
                    if (_activeProfiles.TryGetValue(profileId, out var activeProfile))
                    {
                        _cache.Set(cacheKey, activeProfile, _config.ProfileCacheDuration);
                        return activeProfile;
                    }
                }

                // Try to load from storage;
                var storedProfile = await LoadProfileFromStorageAsync(profileId);
                if (storedProfile != null)
                {
                    lock (_profileLock)
                    {
                        _activeProfiles[profileId] = storedProfile;
                    }
                    _cache.Set(cacheKey, storedProfile, _config.ProfileCacheDuration);
                    return storedProfile;
                }

                // Create new profile;
                CharacterProfile newProfile;
                if (_config.PresetProfiles != null && _config.PresetProfiles.ContainsKey(profileId))
                {
                    // Use preset profile;
                    newProfile = CloneProfileObject(_config.PresetProfiles[profileId]);
                    newProfile.ProfileId = profileId;
                    _logger.LogInformation("Created profile {ProfileId} from preset", profileId);
                }
                else if (profileId == _config.DefaultCharacterId)
                {
                    // Create default profile;
                    newProfile = CreateDefaultProfile();
                    _logger.LogInformation("Created default profile {ProfileId}", profileId);
                }
                else;
                {
                    // Create custom profile based on user if provided;
                    newProfile = await CreateCustomProfileAsync(profileId, userId);
                    _logger.LogInformation("Created custom profile {ProfileId} for user {UserId}", profileId, userId);
                }

                // Initialize profile;
                newProfile.CreatedAt = DateTime.UtcNow;
                newProfile.LastUpdated = DateTime.UtcNow;
                newProfile.InteractionHistory = new Dictionary<string, int>();
                newProfile.RecentBehaviors = new List<BehaviorSample>();

                // Apply constraints;
                ApplyPersonalityConstraints(newProfile);

                // Store profile;
                await StoreProfileAsync(newProfile);

                lock (_profileLock)
                {
                    _activeProfiles[profileId] = newProfile;
                }

                _cache.Set(cacheKey, newProfile, _config.ProfileCacheDuration);

                // Publish event;
                await PublishProfileCreatedEventAsync(newProfile, userId);

                return newProfile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting/creating profile {ProfileId}", profileId);
                throw new CharacterBuilderException($"Failed to get/create profile {profileId}", ex);
            }
        }

        /// <summary>
        /// Adapt profile based on request;
        /// </summary>
        public async Task<CharacterProfile> AdaptProfileAsync(CharacterAdaptationRequest request)
        {
            try
            {
                ValidateAdaptationRequest(request);

                var profile = await GetOrCreateProfileAsync(request.ProfileId, request.UserId);

                // Check if adaptation is needed;
                if (!ShouldAdaptProfile(profile, request))
                {
                    _logger.LogDebug("Skipping adaptation for profile {ProfileId}, conditions not met", request.ProfileId);
                    return profile;
                }

                // Create adapted profile;
                var adaptedProfile = CloneProfileObject(profile);
                adaptedProfile.LastUpdated = DateTime.UtcNow;

                // Apply adaptations;
                await ApplyAdaptationsAsync(adaptedProfile, request);

                // Validate adaptations;
                ValidateAdaptations(adaptedProfile, request);

                // Apply constraints;
                ApplyPersonalityConstraints(adaptedProfile);

                // Check consistency;
                await ValidateProfileConsistencyAsync(request.ProfileId);

                // Store adapted profile;
                await StoreProfileAsync(adaptedProfile);

                // Update cache;
                var cacheKey = $"{ProfileCachePrefix}{request.ProfileId}";
                _cache.Set(cacheKey, adaptedProfile, _config.ProfileCacheDuration);

                lock (_profileLock)
                {
                    _activeProfiles[request.ProfileId] = adaptedProfile;
                }

                // Publish adaptation event;
                await PublishProfileAdaptedEventAsync(profile, adaptedProfile, request);

                // Record metrics;
                await RecordAdaptationMetricsAsync(profile, adaptedProfile, request);

                _logger.LogInformation("Adapted profile {ProfileId} for user {UserId}",
                    request.ProfileId, request.UserId);

                return adaptedProfile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adapting profile {ProfileId}", request?.ProfileId);
                throw new CharacterBuilderException($"Failed to adapt profile {request?.ProfileId}", ex);
            }
        }

        /// <summary>
        /// Clone existing profile;
        /// </summary>
        public async Task<CharacterProfile> CloneProfileAsync(string sourceProfileId, string newProfileId, string newName = null)
        {
            try
            {
                ValidateProfileId(sourceProfileId);
                ValidateProfileId(newProfileId);

                if (sourceProfileId == newProfileId)
                    throw new ArgumentException("Source and target profile IDs must be different");

                var sourceProfile = await GetOrCreateProfileAsync(sourceProfileId);

                var clonedProfile = CloneProfileObject(sourceProfile);
                clonedProfile.ProfileId = newProfileId;
                clonedProfile.Name = newName ?? $"{sourceProfile.Name} (Clone)";
                clonedProfile.CreatedAt = DateTime.UtcNow;
                clonedProfile.LastUpdated = DateTime.UtcNow;

                // Reset adaptation data;
                clonedProfile.TotalInteractions = 0;
                clonedProfile.InteractionHistory = new Dictionary<string, int>();
                clonedProfile.RecentBehaviors = new List<BehaviorSample>();

                // Store cloned profile;
                await StoreProfileAsync(clonedProfile);

                lock (_profileLock)
                {
                    _activeProfiles[newProfileId] = clonedProfile;
                }

                var cacheKey = $"{ProfileCachePrefix}{newProfileId}";
                _cache.Set(cacheKey, clonedProfile, _config.ProfileCacheDuration);

                await PublishProfileClonedEventAsync(sourceProfile, clonedProfile);

                _logger.LogInformation("Cloned profile {SourceId} to {TargetId}", sourceProfileId, newProfileId);

                return clonedProfile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cloning profile from {SourceId} to {TargetId}", sourceProfileId, newProfileId);
                throw new CharacterBuilderException($"Failed to clone profile {sourceProfileId} to {newProfileId}", ex);
            }
        }

        /// <summary>
        /// Update profile;
        /// </summary>
        public async Task<bool> UpdateProfileAsync(CharacterProfile profile)
        {
            try
            {
                ValidateProfile(profile);

                // Apply constraints before saving;
                ApplyPersonalityConstraints(profile);

                profile.LastUpdated = DateTime.UtcNow;

                // Store updated profile;
                await StoreProfileAsync(profile);

                // Update cache;
                var cacheKey = $"{ProfileCachePrefix}{profile.ProfileId}";
                _cache.Set(cacheKey, profile, _config.ProfileCacheDuration);

                lock (_profileLock)
                {
                    _activeProfiles[profile.ProfileId] = profile;
                }

                await PublishProfileUpdatedEventAsync(profile);

                _logger.LogInformation("Updated profile {ProfileId}", profile.ProfileId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating profile {ProfileId}", profile?.ProfileId);
                throw new CharacterBuilderException($"Failed to update profile {profile?.ProfileId}", ex);
            }
        }

        /// <summary>
        /// Delete profile;
        /// </summary>
        public async Task<bool> DeleteProfileAsync(string profileId)
        {
            try
            {
                ValidateProfileId(profileId);

                // Remove from storage;
                await DeleteProfileFromStorageAsync(profileId);

                // Remove from cache;
                var cacheKey = $"{ProfileCachePrefix}{profileId}";
                _cache.Remove(cacheKey);

                // Remove from active profiles;
                lock (_profileLock)
                {
                    _activeProfiles.Remove(profileId);
                }

                await PublishProfileDeletedEventAsync(profileId);

                _logger.LogInformation("Deleted profile {ProfileId}", profileId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting profile {ProfileId}", profileId);
                throw new CharacterBuilderException($"Failed to delete profile {profileId}", ex);
            }
        }

        /// <summary>
        /// Determine optimal communication style;
        /// </summary>
        public async Task<CommunicationStyle> DetermineOptimalStyleAsync(string profileId, ConversationContext context)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);

                // Get base style from profile;
                var baseStyle = profile.GetDominantStyle();

                // Adjust based on context;
                var adjustedStyle = AdjustStyleForContext(baseStyle, context, profile);

                // Apply randomness for variation (if enabled)
                if (profile.Openness > 0.7 && _random.NextDouble() < 0.2)
                {
                    adjustedStyle = GetVariationStyle(adjustedStyle, profile);
                }

                // Validate style against archetype;
                if (_config.ConsistencyRules?.EnforceArchetypeConsistency == true)
                {
                    adjustedStyle = EnsureArchetypeConsistency(adjustedStyle, profile);
                }

                return adjustedStyle;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error determining optimal style for profile {ProfileId}", profileId);
                return CommunicationStyle.Personal; // Fallback style;
            }
        }

        /// <summary>
        /// Determine humor style;
        /// </summary>
        public async Task<HumorStyle> DetermineHumorStyleAsync(string profileId, ConversationContext context)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);

                // Check if humor is appropriate;
                if (!IsHumorAppropriate(context, profile))
                {
                    return HumorStyle.None;
                }

                // Calculate humor probability;
                var humorProbability = profile.HumorFrequency *
                                      (1.0 + profile.Extraversion * 0.3) *
                                      (1.0 - (context.EmotionalState?.Intensity ?? 0.5));

                if (_random.NextDouble() > humorProbability)
                {
                    return HumorStyle.None;
                }

                // Select humor style;
                var style = profile.PreferredHumorStyle;

                // Adjust based on context;
                if (context.Urgency == UrgencyLevel.High)
                    style = HumorStyle.Light; // Keep it light for urgent situations;

                if (context.RequiredFormality == FormalityLevel.High)
                    style = HumorStyle.Witty; // More intellectual humor for formal contexts;

                return style;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error determining humor style for profile {ProfileId}", profileId);
                return HumorStyle.None;
            }
        }

        /// <summary>
        /// Calculate formality level;
        /// </summary>
        public async Task<double> CalculateFormalityLevelAsync(string profileId, ConversationContext context)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);

                var baseFormality = profile.FormalityLevel;

                // Adjust based on context;
                var contextAdjustment = GetFormalityAdjustment(context);

                // Adjust based on user if available;
                var userAdjustment = 0.0;
                if (context.ContextData?.TryGetValue("UserFormalityPreference", out var userPref) == true)
                {
                    userAdjustment = (Convert.ToDouble(userPref) - baseFormality) * 0.3;
                }

                // Calculate final formality;
                var finalFormality = baseFormality + contextAdjustment + userAdjustment;

                // Clamp to valid range;
                return Math.Clamp(finalFormality, 0.0, 1.0);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error calculating formality for profile {ProfileId}", profileId);
                return 0.5;
            }
        }

        /// <summary>
        /// Search profiles by criteria;
        /// </summary>
        public async Task<List<CharacterProfile>> SearchProfilesAsync(ProfileSearchCriteria criteria)
        {
            try
            {
                var allProfiles = await LoadAllProfilesAsync();

                var query = allProfiles.AsQueryable();

                if (criteria.Archetypes?.Any() == true)
                {
                    query = query.Where(p => criteria.Archetypes.Contains(p.PrimaryArchetype));
                }

                if (criteria.OpennessRange.HasValue)
                {
                    query = query.Where(p => criteria.OpennessRange.Value.Contains(p.Openness));
                }

                if (criteria.ExtraversionRange.HasValue)
                {
                    query = query.Where(p => criteria.ExtraversionRange.Value.Contains(p.Extraversion));
                }

                if (criteria.AgreeablenessRange.HasValue)
                {
                    query = query.Where(p => criteria.AgreeablenessRange.Value.Contains(p.Agreeableness));
                }

                if (criteria.PreferredStyle.HasValue)
                {
                    query = query.Where(p => p.GetDominantStyle() == criteria.PreferredStyle.Value);
                }

                if (criteria.MinInteractions > 0)
                {
                    query = query.Where(p => p.TotalInteractions >= criteria.MinInteractions);
                }

                if (criteria.CreatedAfter.HasValue)
                {
                    query = query.Where(p => p.CreatedAt >= criteria.CreatedAfter.Value);
                }

                if (!string.IsNullOrWhiteSpace(criteria.NameContains))
                {
                    query = query.Where(p => p.Name.Contains(criteria.NameContains, StringComparison.OrdinalIgnoreCase));
                }

                return query.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching profiles with criteria");
                return new List<CharacterProfile>();
            }
        }

        /// <summary>
        /// Analyze profile compatibility with user;
        /// </summary>
        public async Task<Dictionary<string, double>> AnalyzeProfileCompatibilityAsync(string profileId, UserPersonality userPersonality)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);
                var compatibility = new Dictionary<string, double>();

                // Calculate personality compatibility (similarity scores)
                compatibility["Openness"] = CalculateSimilarityScore(profile.Openness, userPersonality.Openness);
                compatibility["Conscientiousness"] = CalculateSimilarityScore(profile.Conscientiousness, userPersonality.Conscientiousness);
                compatibility["Extraversion"] = CalculateSimilarityScore(profile.Extraversion, userPersonality.Extraversion);
                compatibility["Agreeableness"] = CalculateSimilarityScore(profile.Agreeableness, userPersonality.Agreeableness);
                compatibility["Neuroticism"] = CalculateComplementaryScore(profile.Neuroticism, userPersonality.Neuroticism);

                // Style compatibility;
                var styleMatch = profile.StylePreferences?
                    .FirstOrDefault(kv => kv.Key == userPersonality.PreferredStyle)
                    .Value ?? 0.5;
                compatibility["CommunicationStyle"] = styleMatch;

                // Formality compatibility;
                compatibility["Formality"] = 1.0 - Math.Abs(profile.FormalityLevel - userPersonality.FormalityPreference);

                // Overall compatibility score (weighted average)
                var weights = new Dictionary<string, double>
                {
                    ["Openness"] = 0.15,
                    ["Conscientiousness"] = 0.15,
                    ["Extraversion"] = 0.20,
                    ["Agreeableness"] = 0.25,
                    ["Neuroticism"] = 0.10,
                    ["CommunicationStyle"] = 0.10,
                    ["Formality"] = 0.05;
                };

                compatibility["Overall"] = weights.Sum(w => compatibility[w.Key] * w.Value);

                return compatibility;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error analyzing compatibility for profile {ProfileId}", profileId);
                return new Dictionary<string, double> { ["Overall"] = 0.5 };
            }
        }

        /// <summary>
        /// Get behavior history;
        /// </summary>
        public async Task<List<BehaviorSample>> GetBehaviorHistoryAsync(string profileId, DateTime? startDate = null, int limit = 100)
        {
            try
            {
                var cacheKey = $"{BehaviorCachePrefix}{profileId}";

                if (_cache.TryGetValue(cacheKey, out List<BehaviorSample> cachedBehaviors))
                {
                    return FilterBehaviors(cachedBehaviors, startDate, limit);
                }

                // Load from storage;
                var storedBehaviors = await LoadBehaviorsFromStorageAsync(profileId);

                // Cache for future use;
                _cache.Set(cacheKey, storedBehaviors, TimeSpan.FromMinutes(30));

                return FilterBehaviors(storedBehaviors, startDate, limit);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting behavior history for profile {ProfileId}", profileId);
                return new List<BehaviorSample>();
            }
        }

        /// <summary>
        /// Train on interaction;
        /// </summary>
        public async Task TrainOnInteractionAsync(string profileId, BehaviorSample interaction)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);

                // Update interaction counts;
                profile.TotalInteractions++;

                if (!profile.InteractionHistory.ContainsKey(interaction.UserId))
                    profile.InteractionHistory[interaction.UserId] = 0;
                profile.InteractionHistory[interaction.UserId]++;

                // Add to recent behaviors (with limit)
                profile.RecentBehaviors.Add(interaction);
                if (profile.RecentBehaviors.Count > 100)
                {
                    profile.RecentBehaviors = profile.RecentBehaviors;
                        .OrderByDescending(b => b.Timestamp)
                        .Take(50)
                        .ToList();
                }

                // Update profile based on interaction feedback;
                if (Math.Abs(interaction.UserResponseScore) > 0.1)
                {
                    await UpdateProfileFromFeedbackAsync(profile, interaction);
                }

                // Update profile;
                profile.LastUpdated = DateTime.UtcNow;
                await UpdateProfileAsync(profile);

                // Store behavior sample;
                await StoreBehaviorSampleAsync(profileId, interaction);

                // Clear behavior cache;
                var cacheKey = $"{BehaviorCachePrefix}{profileId}";
                _cache.Remove(cacheKey);

                _logger.LogDebug("Trained profile {ProfileId} on interaction {InteractionId}",
                    profileId, interaction.InteractionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error training profile {ProfileId} on interaction", profileId);
            }
        }

        /// <summary>
        /// Reset profile adaptation;
        /// </summary>
        public async Task ResetProfileAdaptationAsync(string profileId)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);

                // Reset adaptation data;
                profile.TotalInteractions = 0;
                profile.InteractionHistory.Clear();
                profile.RecentBehaviors.Clear();
                profile.LastUpdated = DateTime.UtcNow;

                // Reset to original personality if preset exists;
                if (_config.PresetProfiles != null && _config.PresetProfiles.ContainsKey(profileId))
                {
                    var preset = _config.PresetProfiles[profileId];
                    profile.Openness = preset.Openness;
                    profile.Conscientiousness = preset.Conscientiousness;
                    profile.Extraversion = preset.Extraversion;
                    profile.Agreeableness = preset.Agreeableness;
                    profile.Neuroticism = preset.Neuroticism;
                }

                await UpdateProfileAsync(profile);

                _logger.LogInformation("Reset adaptation for profile {ProfileId}", profileId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resetting adaptation for profile {ProfileId}", profileId);
                throw new CharacterBuilderException($"Failed to reset adaptation for profile {profileId}", ex);
            }
        }

        /// <summary>
        /// Generate character from template;
        /// </summary>
        public async Task<CharacterProfile> GenerateCharacterFromTemplateAsync(string templateName, Dictionary<string, object> parameters)
        {
            try
            {
                var template = await LoadTemplateAsync(templateName);
                if (template == null)
                    throw new CharacterBuilderException($"Template {templateName} not found");

                var profile = ApplyTemplate(template, parameters);

                // Generate unique ID;
                profile.ProfileId = $"template_{templateName}_{Guid.NewGuid():N}";
                profile.CreatedAt = DateTime.UtcNow;
                profile.LastUpdated = DateTime.UtcNow;

                // Store the generated profile;
                await StoreProfileAsync(profile);

                _logger.LogInformation("Generated character from template {TemplateName} as {ProfileId}",
                    templateName, profile.ProfileId);

                return profile;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating character from template {TemplateName}", templateName);
                throw new CharacterBuilderException($"Failed to generate character from template {templateName}", ex);
            }
        }

        /// <summary>
        /// Validate profile consistency;
        /// </summary>
        public async Task ValidateProfileConsistencyAsync(string profileId)
        {
            try
            {
                var profile = await GetOrCreateProfileAsync(profileId);
                var inconsistencies = new List<string>();

                // Check personality trait consistency;
                inconsistencies.AddRange(ValidatePersonalityTraits(profile));

                // Check style consistency;
                inconsistencies.AddRange(ValidateStyleConsistency(profile));

                // Check archetype consistency;
                inconsistencies.AddRange(ValidateArchetypeConsistency(profile));

                // Check behavioral consistency;
                inconsistencies.AddRange(ValidateBehavioralConsistency(profile));

                if (inconsistencies.Any())
                {
                    _logger.LogWarning("Profile {ProfileId} has {Count} inconsistencies: {Inconsistencies}",
                        profileId, inconsistencies.Count, string.Join("; ", inconsistencies));

                    // Auto-correct if possible;
                    await AutoCorrectInconsistenciesAsync(profile, inconsistencies);
                }
                else;
                {
                    _logger.LogDebug("Profile {ProfileId} passed consistency validation", profileId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating consistency for profile {ProfileId}", profileId);
            }
        }

        #region Private Methods;

        /// <summary>
        /// Initialize preset profiles;
        /// </summary>
        private void InitializePresetProfiles()
        {
            if (_config.PresetProfiles == null)
            {
                _config.PresetProfiles = new Dictionary<string, CharacterProfile>
                {
                    ["wise_mentor"] = CreateMentorProfile(),
                    ["friendly_companion"] = CreateCompanionProfile(),
                    ["technical_expert"] = CreateExpertProfile(),
                    ["creative_guide"] = CreateCreativeProfile(),
                    ["protective_ally"] = CreateProtectorProfile(),
                    ["diplomatic_adviser"] = CreateDiplomatProfile(),
                    ["enthusiastic_helper"] = CreateEnthusiastProfile(),
                    ["analytical_assistant"] = CreateAnalystProfile()
                };
            }
        }

        /// <summary>
        /// Load configuration;
        /// </summary>
        private void LoadConfiguration()
        {
            try
            {
                var configSection = _settingsManager.GetSection("CharacterBuilder");
                if (configSection != null)
                {
                    // Load additional configuration if available;
                    _logger.LogDebug("Loaded character builder configuration");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load character builder configuration, using defaults");
            }
        }

        /// <summary>
        /// Subscribe to events;
        /// </summary>
        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<UserInteractionEvent>(HandleUserInteraction);
            _eventBus.Subscribe<PersonalityAnalysisEvent>(HandlePersonalityAnalysis);
        }

        /// <summary>
        /// Create default configuration;
        /// </summary>
        private CharacterConfig CreateDefaultConfig()
        {
            return new CharacterConfig;
            {
                DefaultCharacterId = "default",
                EnableDynamicAdaptation = true,
                AdaptationRate = 0.1,
                MinInteractionThreshold = 10,
                ProfileCacheDuration = TimeSpan.FromHours(1),
                PersonalityConstraints = new PersonalityConstraints;
                {
                    MinOpenness = 0.2,
                    MaxOpenness = 0.9,
                    MinConscientiousness = 0.3,
                    MaxConscientiousness = 0.95,
                    MinExtraversion = 0.1,
                    MaxExtraversion = 0.8,
                    MinAgreeableness = 0.4,
                    MaxAgreeableness = 1.0,
                    MinNeuroticism = 0.0,
                    MaxNeuroticism = 0.5;
                },
                ConsistencyRules = new StyleConsistencyRules;
                {
                    MaxStyleDrift = 0.3,
                    ConsistencyWindow = 50,
                    EnforceArchetypeConsistency = true,
                    ArchetypeStyleMapping = new Dictionary<CharacterArchetype, List<CommunicationStyle>>
                    {
                        [CharacterArchetype.Mentor] = new() { CommunicationStyle.Analytical, CommunicationStyle.Personal },
                        [CharacterArchetype.Companion] = new() { CommunicationStyle.Personal, CommunicationStyle.Functional },
                        [CharacterArchetype.Expert] = new() { CommunicationStyle.Analytical, CommunicationStyle.Functional },
                        [CharacterArchetype.Creative] = new() { CommunicationStyle.Intuitive, CommunicationStyle.Personal },
                        [CharacterArchetype.Protector] = new() { CommunicationStyle.Personal, CommunicationStyle.Functional },
                        [CharacterArchetype.Diplomat] = new() { CommunicationStyle.Personal, CommunicationStyle.Analytical },
                        [CharacterArchetype.Enthusiast] = new() { CommunicationStyle.Personal, CommunicationStyle.Intuitive },
                        [CharacterArchetype.Analyst] = new() { CommunicationStyle.Analytical, CommunicationStyle.Functional }
                    }
                }
            };
        }

        /// <summary>
        /// Create default profile;
        /// </summary>
        private CharacterProfile CreateDefaultProfile()
        {
            return new CharacterProfile;
            {
                ProfileId = _config.DefaultCharacterId,
                Name = "Default Assistant",
                Description = "Balanced AI assistant character",
                Openness = 0.7,
                Conscientiousness = 0.8,
                Extraversion = 0.5,
                Agreeableness = 0.9,
                Neuroticism = 0.2,
                StylePreferences = new Dictionary<CommunicationStyle, double>
                {
                    [CommunicationStyle.Personal] = 0.8,
                    [CommunicationStyle.Functional] = 0.6,
                    [CommunicationStyle.Analytical] = 0.5,
                    [CommunicationStyle.Intuitive] = 0.4;
                },
                PreferredHumorStyle = HumorStyle.Light,
                HumorFrequency = 0.2,
                PrimaryArchetype = CharacterArchetype.Companion,
                SecondaryArchetypes = new List<CharacterArchetype> { CharacterArchetype.Expert },
                FormalityLevel = 0.5,
                VerbosityLevel = 0.6,
                EmpathyLevel = 0.8,
                Assertiveness = 0.4;
            };
        }

        /// <summary>
        /// Create mentor profile;
        /// </summary>
        private CharacterProfile CreateMentorProfile()
        {
            return new CharacterProfile;
            {
                ProfileId = "wise_mentor",
                Name = "Wise Mentor",
                Description = "Wise and guiding character, provides thoughtful advice",
                Openness = 0.8,
                Conscientiousness = 0.9,
                Extraversion = 0.4,
                Agreeableness = 0.85,
                Neuroticism = 0.15,
                StylePreferences = new Dictionary<CommunicationStyle, double>
                {
                    [CommunicationStyle.Analytical] = 0.9,
                    [CommunicationStyle.Personal] = 0.7,
                    [CommunicationStyle.Functional] = 0.6,
                    [CommunicationStyle.Intuitive] = 0.5;
                },
                PreferredHumorStyle = HumorStyle.Witty,
                HumorFrequency = 0.15,
                PrimaryArchetype = CharacterArchetype.Mentor,
                FormalityLevel = 0.7,
                VerbosityLevel = 0.7,
                EmpathyLevel = 0.8,
                Assertiveness = 0.5;
            };
        }

        /// <summary>
        /// Create companion profile;
        /// </summary>
        private CharacterProfile CreateCompanionProfile()
        {
            return new CharacterProfile;
            {
                ProfileId = "friendly_companion",
                Name = "Friendly Companion",
                Description = "Friendly and supportive companion character",
                Openness = 0.6,
                Conscientiousness = 0.7,
                Extraversion = 0.7,
                Agreeableness = 0.95,
                Neuroticism = 0.2,
                StylePreferences = new Dictionary<CommunicationStyle, double>
                {
                    [CommunicationStyle.Personal] = 0.9,
                    [CommunicationStyle.Functional] = 0.7,
                    [CommunicationStyle.Analytical] = 0.4,
                    [CommunicationStyle.Intuitive] = 0.6;
                },
                PreferredHumorStyle = HumorStyle.Playful,
                HumorFrequency = 0.3,
                PrimaryArchetype = CharacterArchetype.Companion,
                FormalityLevel = 0.3,
                VerbosityLevel = 0.7,
                EmpathyLevel = 0.9,
                Assertiveness = 0.3;
            };
        }

        // Additional preset profile creation methods would follow similar pattern...

        #region Profile Creation Helpers;

        private CharacterProfile CreateExpertProfile() => new() { /* ... */ };
        private CharacterProfile CreateCreativeProfile() => new() { /* ... */ };
        private CharacterProfile CreateProtectorProfile() => new() { /* ... */ };
        private CharacterProfile CreateDiplomatProfile() => new() { /* ... */ };
        private CharacterProfile CreateEnthusiastProfile() => new() { /* ... */ };
        private CharacterProfile CreateAnalystProfile() => new() { /* ... */ };

        #endregion;

        /// <summary>
        /// Create custom profile based on user;
        /// </summary>
        private async Task<CharacterProfile> CreateCustomProfileAsync(string profileId, string userId)
        {
            var profile = CreateDefaultProfile();
            profile.ProfileId = profileId;
            profile.Name = $"Custom Profile {profileId}";

            if (!string.IsNullOrEmpty(userId))
            {
                // Try to adapt to user's personality;
                var userPersonality = await AnalyzeUserPersonalityAsync(userId);
                if (userPersonality != null)
                {
                    // Adjust profile to complement user;
                    await AdjustProfileToUserAsync(profile, userPersonality);
                }
            }

            return profile;
        }

        /// <summary>
        /// Validate profile ID;
        /// </summary>
        private void ValidateProfileId(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                throw new ArgumentException("Profile ID cannot be empty", nameof(profileId));

            if (profileId.Length > 100)
                throw new ArgumentException("Profile ID cannot exceed 100 characters", nameof(profileId));

            if (!System.Text.RegularExpressions.Regex.IsMatch(profileId, @"^[a-zA-Z0-9_-]+$"))
                throw new ArgumentException("Profile ID can only contain letters, numbers, underscores and hyphens", nameof(profileId));
        }

        /// <summary>
        /// Validate profile object;
        /// </summary>
        private void ValidateProfile(CharacterProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            ValidateProfileId(profile.ProfileId);

            if (string.IsNullOrWhiteSpace(profile.Name))
                throw new ArgumentException("Profile name cannot be empty", nameof(profile));

            // Validate personality trait ranges;
            var traits = new[]
            {
                ("Openness", profile.Openness),
                ("Conscientiousness", profile.Conscientiousness),
                ("Extraversion", profile.Extraversion),
                ("Agreeableness", profile.Agreeableness),
                ("Neuroticism", profile.Neuroticism)
            };

            foreach (var (trait, value) in traits)
            {
                if (value < 0.0 || value > 1.0)
                    throw new ArgumentException($"{trait} must be between 0.0 and 1.0, got {value}");
            }
        }

        /// <summary>
        /// Validate adaptation request;
        /// </summary>
        private void ValidateAdaptationRequest(CharacterAdaptationRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ValidateProfileId(request.ProfileId);

            if (string.IsNullOrWhiteSpace(request.UserId))
                throw new ArgumentException("User ID cannot be empty for adaptation", nameof(request));

            if (request.AdaptationGoal == null)
                throw new ArgumentException("Adaptation goal cannot be null", nameof(request));
        }

        /// <summary>
        /// Check if profile should be adapted;
        /// </summary>
        private bool ShouldAdaptProfile(CharacterProfile profile, CharacterAdaptationRequest request)
        {
            if (!_config.EnableDynamicAdaptation)
                return false;

            // Check minimum interaction threshold;
            if (profile.TotalInteractions < _config.MinInteractionThreshold)
                return false;

            // Check if we've adapted recently for this user;
            if (profile.InteractionHistory.TryGetValue(request.UserId, out var userInteractions))
            {
                if (userInteractions < 3) // Need at least 3 interactions with this user;
                    return false;
            }

            // Check adaptation goal priority;
            return request.AdaptationGoal?.Type switch;
            {
                GoalType.IncreaseRapport => true,
                GoalType.MatchUserStyle => true,
                GoalType.MaintainConsistency => false, // Don't adapt for consistency;
                _ => profile.TotalInteractions % 10 == 0 // Adapt every 10th interaction for other goals;
            };
        }

        /// <summary>
        /// Apply adaptations to profile;
        /// </summary>
        private async Task ApplyAdaptationsAsync(CharacterProfile profile, CharacterAdaptationRequest request)
        {
            var adaptationRate = _config.AdaptationRate;

            switch (request.AdaptationGoal.Type)
            {
                case GoalType.IncreaseRapport:
                    await AdaptForRapportAsync(profile, request, adaptationRate);
                    break;

                case GoalType.MatchUserStyle:
                    await AdaptToUserStyleAsync(profile, request, adaptationRate);
                    break;

                case GoalType.OptimizeEngagement:
                    await AdaptForEngagementAsync(profile, request, adaptationRate);
                    break;

                case GoalType.EnhanceTrust:
                    await AdaptForTrustAsync(profile, request, adaptationRate);
                    break;

                case GoalType.ImproveEfficiency:
                    await AdaptForEfficiencyAsync(profile, request, adaptationRate);
                    break;
            }

            // Apply any additional constraints;
            if (request.Constraints != null)
            {
                ApplyConstraintAdaptations(profile, request.Constraints);
            }
        }

        /// <summary>
        /// Adapt profile for rapport building;
        /// </summary>
        private async Task AdaptForRapportAsync(CharacterProfile profile, CharacterAdaptationRequest request, double rate)
        {
            if (request.UserPersonality == null)
                return;

            // Move personality traits toward user's traits;
            profile.Openness = MoveToward(profile.Openness, request.UserPersonality.Openness, rate * 0.5);
            profile.Extraversion = MoveToward(profile.Extraversion, request.UserPersonality.Extraversion, rate * 0.7);
            profile.Agreeableness = MoveToward(profile.Agreeableness, request.UserPersonality.Agreeableness, rate * 0.3);

            // Adjust style preferences;
            if (profile.StylePreferences != null && profile.StylePreferences.ContainsKey(request.UserPersonality.PreferredStyle))
            {
                profile.StylePreferences[request.UserPersonality.PreferredStyle] += rate * 0.2;
            }

            // Adjust formality toward user preference;
            profile.FormalityLevel = MoveToward(profile.FormalityLevel, request.UserPersonality.FormalityPreference, rate * 0.4);

            // Increase empathy slightly for rapport;
            profile.EmpathyLevel = Math.Min(profile.EmpathyLevel + rate * 0.1, 1.0);
        }

        /// <summary>
        /// Adapt profile to match user style;
        /// </summary>
        private async Task AdaptToUserStyleAsync(CharacterProfile profile, CharacterAdaptationRequest request, double rate)
        {
            // Similar to rapport but more focused on communication style;
            if (request.UserPersonality == null)
                return;

            // Stronger adjustment for communication style;
            if (profile.StylePreferences != null)
            {
                // Boost preferred style;
                foreach (var style in profile.StylePreferences.Keys.ToList())
                {
                    if (style == request.UserPersonality.PreferredStyle)
                        profile.StylePreferences[style] += rate * 0.3;
                    else;
                        profile.StylePreferences[style] -= rate * 0.1;
                }

                // Ensure values stay in valid range;
                NormalizeStylePreferences(profile);
            }

            // Adjust verbosity based on conversation context;
            if (request.ConversationContext != null)
            {
                var optimalVerbosity = CalculateOptimalVerbosity(request.ConversationContext);
                profile.VerbosityLevel = MoveToward(profile.VerbosityLevel, optimalVerbosity, rate * 0.2);
            }
        }

        // Additional adaptation methods would follow similar pattern...

        /// <summary>
        /// Move value toward target;
        /// </summary>
        private double MoveToward(double current, double target, double amount)
        {
            var difference = target - current;
            return current + (difference * amount);
        }

        /// <summary>
        /// Normalize style preferences;
        /// </summary>
        private void NormalizeStylePreferences(CharacterProfile profile)
        {
            if (profile.StylePreferences == null || !profile.StylePreferences.Any())
                return;

            var sum = profile.StylePreferences.Values.Sum();
            if (sum <= 0) return;

            var normalized = profile.StylePreferences.ToDictionary(
                kv => kv.Key,
                kv => kv.Value / sum);

            profile.StylePreferences = normalized;
        }

        /// <summary>
        /// Validate adaptations;
        /// </summary>
        private void ValidateAdaptations(CharacterProfile profile, CharacterAdaptationRequest request)
        {
            // Check for excessive drift;
            var drift = CalculateProfileDrift(profile, request.OriginalProfile);
            if (drift > _config.ConsistencyRules.MaxStyleDrift)
            {
                _logger.LogWarning("Profile {ProfileId} drift {Drift} exceeds maximum {MaxDrift}",
                    profile.ProfileId, drift, _config.ConsistencyRules.MaxStyleDrift);

                // Scale back adaptations;
                ScaleBackAdaptations(profile, request.OriginalProfile, _config.ConsistencyRules.MaxStyleDrift / drift);
            }
        }

        /// <summary>
        /// Apply personality constraints;
        /// </summary>
        private void ApplyPersonalityConstraints(CharacterProfile profile)
        {
            if (_config.PersonalityConstraints == null)
                return;

            var constraints = _config.PersonalityConstraints;

            profile.Openness = Math.Clamp(profile.Openness, constraints.MinOpenness, constraints.MaxOpenness);
            profile.Conscientiousness = Math.Clamp(profile.Conscientiousness, constraints.MinConscientiousness, constraints.MaxConscientiousness);
            profile.Extraversion = Math.Clamp(profile.Extraversion, constraints.MinExtraversion, constraints.MaxExtraversion);
            profile.Agreeableness = Math.Clamp(profile.Agreeableness, constraints.MinAgreeableness, constraints.MaxAgreeableness);
            profile.Neuroticism = Math.Clamp(profile.Neuroticism, constraints.MinNeuroticism, constraints.MaxNeuroticism);
        }

        /// <summary>
        /// Clone profile object;
        /// </summary>
        private CharacterProfile CloneProfileObject(CharacterProfile source)
        {
            // Simple clone using serialization for deep copy;
            var json = JsonSerializer.Serialize(source);
            return JsonSerializer.Deserialize<CharacterProfile>(json);
        }

        /// <summary>
        /// Load profile from storage;
        /// </summary>
        private async Task<CharacterProfile> LoadProfileFromStorageAsync(string profileId)
        {
            try
            {
                var key = $"character_profile_{profileId}";
                return await _longTermMemory.RetrieveAsync<CharacterProfile>(key);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Store profile to storage;
        /// </summary>
        private async Task StoreProfileAsync(CharacterProfile profile)
        {
            var key = $"character_profile_{profile.ProfileId}";
            await _longTermMemory.StoreAsync(key, profile);
        }

        /// <summary>
        /// Delete profile from storage;
        /// </summary>
        private async Task DeleteProfileFromStorageAsync(string profileId)
        {
            var key = $"character_profile_{profileId}";
            await _longTermMemory.DeleteAsync(key);
        }

        /// <summary>
        /// Load all profiles from storage;
        /// </summary>
        private async Task<List<CharacterProfile>> LoadAllProfilesAsync()
        {
            try
            {
                var pattern = "character_profile_*";
                return await _longTermMemory.SearchAsync<CharacterProfile>(pattern);
            }
            catch
            {
                return new List<CharacterProfile>();
            }
        }

        // Additional helper methods would continue...

        #endregion;

        #region Event Handlers;

        private async Task HandleUserInteraction(UserInteractionEvent @event)
        {
            try
            {
                // Create behavior sample from event;
                var sample = new BehaviorSample;
                {
                    Timestamp = @event.Timestamp,
                    InteractionId = @event.InteractionId,
                    UserId = @event.UserId,
                    Context = @event.Context,
                    UsedStyle = @event.CommunicationStyle,
                    UserResponseScore = @event.EngagementScore,
                    BehavioralMetrics = @event.Metrics;
                };

                // Train relevant profiles;
                await TrainOnInteractionAsync(@event.ProfileId, sample);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling user interaction event");
            }
        }

        private async Task HandlePersonalityAnalysis(PersonalityAnalysisEvent @event)
        {
            try
            {
                // Update user personality models;
                // This could trigger profile adaptation;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling personality analysis event");
            }
        }

        #endregion;

        #region Event Publishing;

        private async Task PublishProfileCreatedEventAsync(CharacterProfile profile, string userId)
        {
            var @event = new ProfileCreatedEvent;
            {
                ProfileId = profile.ProfileId,
                ProfileName = profile.Name,
                UserId = userId,
                Timestamp = DateTime.UtcNow,
                Archetype = profile.PrimaryArchetype;
            };

            await _eventBus.PublishAsync(@event);
        }

        private async Task PublishProfileAdaptedEventAsync(CharacterProfile original, CharacterProfile adapted, CharacterAdaptationRequest request)
        {
            var @event = new ProfileAdaptedEvent;
            {
                ProfileId = adapted.ProfileId,
                UserId = request.UserId,
                Timestamp = DateTime.UtcNow,
                AdaptationGoal = request.AdaptationGoal.Type.ToString(),
                PersonalityChanges = CalculatePersonalityChanges(original, adapted),
                StyleChanges = CalculateStyleChanges(original, adapted)
            };

            await _eventBus.PublishAsync(@event);
        }

        // Additional event publishing methods...

        #endregion;

        #region Metrics Recording;

        private async Task RecordAdaptationMetricsAsync(CharacterProfile original, CharacterProfile adapted, CharacterAdaptationRequest request)
        {
            var metrics = new Dictionary<string, object>
            {
                ["profile_id"] = request.ProfileId,
                ["user_id"] = request.UserId,
                ["adaptation_goal"] = request.AdaptationGoal.Type.ToString(),
                ["personality_drift"] = CalculateProfileDrift(original, adapted),
                ["openness_change"] = adapted.Openness - original.Openness,
                ["extraversion_change"] = adapted.Extraversion - original.Extraversion,
                ["agreeableness_change"] = adapted.Agreeableness - original.Agreeableness,
                ["interaction_count"] = adapted.TotalInteractions;
            };

            await _metricsCollector.RecordMetricAsync("character_adaptation", metrics);
        }

        #endregion;
    }

    #region Exception Classes;

    public class CharacterBuilderException : Exception
    {
        public CharacterBuilderException(string message) : base(message) { }
        public CharacterBuilderException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Event Definitions;

    public class UserInteractionEvent : IEvent;
    {
        public string InteractionId { get; set; }
        public string ProfileId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public string Context { get; set; }
        public CommunicationStyle CommunicationStyle { get; set; }
        public double EngagementScore { get; set; }
        public Dictionary<string, object> Metrics { get; set; }
    }

    public class PersonalityAnalysisEvent : IEvent;
    {
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public UserPersonality Personality { get; set; }
        public double Confidence { get; set; }
    }

    public class ProfileCreatedEvent : IEvent;
    {
        public string ProfileId { get; set; }
        public string ProfileName { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public CharacterArchetype Archetype { get; set; }
    }

    public class ProfileAdaptedEvent : IEvent;
    {
        public string ProfileId { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
        public string AdaptationGoal { get; set; }
        public Dictionary<string, double> PersonalityChanges { get; set; }
        public Dictionary<string, double> StyleChanges { get; set; }
    }

    public class ProfileClonedEvent : IEvent;
    {
        public string SourceProfileId { get; set; }
        public string TargetProfileId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ProfileUpdatedEvent : IEvent;
    {
        public string ProfileId { get; set; }
        public DateTime Timestamp { get; set; }
        public string UpdateType { get; set; }
    }

    public class ProfileDeletedEvent : IEvent;
    {
        public string ProfileId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Dependency Injection Extension;

    public static class CharacterBuilderExtensions;
    {
        public static IServiceCollection AddCharacterBuilder(this IServiceCollection services, IConfiguration configuration)
        {
            services.Configure<CharacterConfig>(configuration.GetSection("CharacterBuilder"));

            services.AddSingleton<ICharacterBuilder, CharacterBuilder>();

            // Register memory cache if not already registered;
            if (!services.Any(x => x.ServiceType == typeof(IMemoryCache)))
            {
                services.AddMemoryCache();
            }

            return services;
        }
    }

    #endregion;

    #region Configuration Example;

    /*
    {
      "CharacterBuilder": {
        "DefaultCharacterId": "default",
        "EnableDynamicAdaptation": true,
        "AdaptationRate": 0.1,
        "MinInteractionThreshold": 10,
        "ProfileCacheDuration": "01:00:00",
        "PersonalityConstraints": {
          "MinOpenness": 0.2,
          "MaxOpenness": 0.9,
          "MinConscientiousness": 0.3,
          "MaxConscientiousness": 0.95,
          "MinExtraversion": 0.1,
          "MaxExtraversion": 0.8,
          "MinAgreeableness": 0.4,
          "MaxAgreeableness": 1.0,
          "MinNeuroticism": 0.0,
          "MaxNeuroticism": 0.5;
        },
        "ConsistencyRules": {
          "MaxStyleDrift": 0.3,
          "ConsistencyWindow": 50,
          "EnforceArchetypeConsistency": true;
        },
        "PresetProfiles": {
          "wise_mentor": {
            "Name": "Wise Mentor",
            "Description": "Wise and guiding character",
            "Openness": 0.8,
            "Conscientiousness": 0.9,
            "Extraversion": 0.4,
            "Agreeableness": 0.85,
            "Neuroticism": 0.15,
            "PrimaryArchetype": "Mentor"
          }
        }
      }
    }
    */

    #endregion;
}
