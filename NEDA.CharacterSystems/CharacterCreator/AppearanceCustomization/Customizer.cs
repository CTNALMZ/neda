using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.Brain.MemorySystem;
using NEDA.CharacterSystems.CharacterCreator.MorphTargets;
using NEDA.CharacterSystems.CharacterCreator.OutfitSystems;
using NEDA.Core.Common;
using NEDA.Monitoring.MetricsCollector;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using static NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization.Customizer;

namespace NEDA.CharacterSystems.CharacterCreator.AppearanceCustomization;
{
    /// <summary>
    /// Represents a character customization option with visual and statistical properties;
    /// </summary>
    public class CustomizationOption;
    {
        [JsonPropertyName("id")]
        public string OptionId { get; set; }

        [JsonPropertyName("name")]
        public string OptionName { get; set; }

        [JsonPropertyName("category")]
        public CustomizationCategory Category { get; set; }

        [JsonPropertyName("subcategory")]
        public string Subcategory { get; set; }

        [JsonPropertyName("type")]
        public OptionType Type { get; set; }

        [JsonPropertyName("values")]
        public Dictionary<string, object> Values { get; set; }

        [JsonPropertyName("minValue")]
        public float MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public float MaxValue { get; set; }

        [JsonPropertyName("defaultValue")]
        public float DefaultValue { get; set; }

        [JsonPropertyName("stepSize")]
        public float StepSize { get; set; }

        [JsonPropertyName("visualData")]
        public VisualData VisualData { get; set; }

        [JsonPropertyName("statModifiers")]
        public List<StatModifier> StatModifiers { get; set; }

        [JsonPropertyName("requirements")]
        public List<Requirement> Requirements { get; set; }

        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; }

        [JsonPropertyName("cost")]
        public Cost Cost { get; set; }

        [JsonPropertyName("unlockLevel")]
        public int UnlockLevel { get; set; }

        [JsonPropertyName("isPremium")]
        public bool IsPremium { get; set; }

        [JsonPropertyName("compatibility")]
        public List<string> Compatibility { get; set; }

        public CustomizationOption()
        {
            Values = new Dictionary<string, object>();
            StatModifiers = new List<StatModifier>();
            Requirements = new List<Requirement>();
            Tags = new List<string>();
            Compatibility = new List<string>();
            VisualData = new VisualData();
            Cost = new Cost();
        }
    }

    /// <summary>
    /// Represents a complete character customization profile;
    /// </summary>
    public class CharacterCustomization;
    {
        [JsonPropertyName("characterId")]
        public string CharacterId { get; set; }

        [JsonPropertyName("presetId")]
        public string PresetId { get; set; }

        [JsonPropertyName("customizations")]
        public Dictionary<string, CustomizationSelection> Selections { get; set; }

        [JsonPropertyName("appliedModifiers")]
        public List<AppliedModifier> AppliedModifiers { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("metadata")]
        public Dictionary<string, object> Metadata { get; set; }

        public CharacterCustomization()
        {
            Selections = new Dictionary<string, CustomizationSelection>();
            AppliedModifiers = new List<AppliedModifier>();
            Metadata = new Dictionary<string, object>();
            CreatedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.UtcNow;
            Version = 1;
        }
    }

    /// <summary>
    /// Represents a user's selection for a customization option;
    /// </summary>
    public class CustomizationSelection;
    {
        [JsonPropertyName("optionId")]
        public string OptionId { get; set; }

        [JsonPropertyName("category")]
        public CustomizationCategory Category { get; set; }

        [JsonPropertyName("value")]
        public object Value { get; set; }

        [JsonPropertyName("blendValue")]
        public float BlendValue { get; set; }

        [JsonPropertyName("isActive")]
        public bool IsActive { get; set; }

        [JsonPropertyName("layerOrder")]
        public int LayerOrder { get; set; }

        [JsonPropertyName("overrides")]
        public Dictionary<string, object> Overrides { get; set; }

        public CustomizationSelection()
        {
            Overrides = new Dictionary<string, object>();
            IsActive = true;
            BlendValue = 1.0f;
        }
    }

    /// <summary>
    /// Main Customizer service interface;
    /// </summary>
    public interface ICustomizer;
    {
        // Character Management;
        Task<CharacterCustomization> CreateCharacterAsync(string characterId, string presetId = null);
        Task<CharacterCustomization> LoadCharacterAsync(string characterId);
        Task<bool> SaveCharacterAsync(string characterId);
        Task<bool> DeleteCharacterAsync(string characterId);

        // Customization Operations;
        Task<CustomizationResult> ApplyCustomizationAsync(string characterId, string optionId, object value);
        Task<CustomizationResult> ApplyBatchCustomizationsAsync(string characterId, Dictionary<string, object> customizations);
        Task<CustomizationResult> ResetCustomizationAsync(string characterId, string optionId);
        Task<CustomizationResult> ResetCategoryAsync(string characterId, CustomizationCategory category);
        Task<CustomizationResult> ResetAllCustomizationsAsync(string characterId);

        // Option Management;
        Task<List<CustomizationOption>> GetAvailableOptionsAsync(string characterId, CustomizationCategory? category = null);
        Task<CustomizationOption> GetOptionAsync(string optionId);
        Task<List<CustomizationOption>> SearchOptionsAsync(string query, CustomizationCategory? category = null);
        Task<bool> ValidateCustomizationAsync(string characterId, string optionId, object value);

        // Preview and Visualization;
        Task<CustomizationPreview> GeneratePreviewAsync(string characterId);
        Task<VisualizationData> GenerateVisualizationAsync(string characterId);
        Task<ComparisonResult> CompareCustomizationsAsync(string characterId, string otherCharacterId);

        // Presets and Templates;
        Task<string> SaveAsPresetAsync(string characterId, string presetName);
        Task<bool> ApplyPresetAsync(string characterId, string presetId);
        Task<List<CustomizationPreset>> GetAvailablePresetsAsync();

        // Advanced Features;
        Task<CustomizationResult> RandomizeAsync(string characterId, CustomizationCategory? category = null);
        Task<CustomizationResult> SymmetrizeAsync(string characterId, string optionId);
        Task<CustomizationResult> MirrorAsync(string characterId, CustomizationCategory category);
        Task<GeneticResult> GenerateGeneticVariationsAsync(string characterId, int count);

        // Utility Methods;
        Task<CustomizationStatistics> GetStatisticsAsync(string characterId);
        Task<List<CustomizationHistory>> GetHistoryAsync(string characterId, int limit = 50);
        Task<bool> UndoLastChangeAsync(string characterId);
        Task<bool> RedoLastChangeAsync(string characterId);

        // Integration Methods;
        Task<MorphTargetData> GenerateMorphTargetsAsync(string characterId);
        Task<OutfitConfiguration> GenerateOutfitConfigurationAsync(string characterId);
    }

    /// <summary>
    /// Implementation of the Customizer service with advanced character customization capabilities;
    /// </summary>
    public class Customizer : ICustomizer, IDisposable;
    {
        private readonly ILogger<Customizer> _logger;
        private readonly IEventBus _eventBus;
        private readonly IMetricsCollector _metrics;
        private readonly IMemoryCache _cache;
        private readonly ICustomizationRepository _repository;
        private readonly IOptionProvider _optionProvider;
        private readonly IValidationEngine _validationEngine;
        private readonly IPreviewGenerator _previewGenerator;
        private readonly IBlendShapeEngine _blendShapeEngine;
        private readonly IOutfitManager _outfitManager;

        private readonly CustomizerConfig _config;
        private readonly Dictionary<string, CharacterSession> _activeSessions;
        private readonly Dictionary<string, Stack<CustomizationHistory>> _undoStacks;
        private readonly Dictionary<string, Stack<CustomizationHistory>> _redoStacks;
        private readonly object _sessionLock = new object();

        private const string CACHE_PREFIX = "customizer_";
        private const int MAX_UNDO_STEPS = 50;
        private const int CACHE_DURATION_MINUTES = 30;

        /// <summary>
        /// Customizer constructor with dependency injection;
        /// </summary>
        public Customizer(
            ILogger<Customizer> logger,
            IEventBus eventBus,
            IMetricsCollector metrics,
            IMemoryCache cache,
            ICustomizationRepository repository,
            IOptionProvider optionProvider,
            IValidationEngine validationEngine,
            IPreviewGenerator previewGenerator,
            IBlendShapeEngine blendShapeEngine,
            IOutfitManager outfitManager,
            CustomizerConfig config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _optionProvider = optionProvider ?? throw new ArgumentNullException(nameof(optionProvider));
            _validationEngine = validationEngine ?? throw new ArgumentNullException(nameof(validationEngine));
            _previewGenerator = previewGenerator ?? throw new ArgumentNullException(nameof(previewGenerator));
            _blendShapeEngine = blendShapeEngine ?? throw new ArgumentNullException(nameof(blendShapeEngine));
            _outfitManager = outfitManager ?? throw new ArgumentNullException(nameof(outfitManager));
            _config = config ?? new CustomizerConfig();

            _activeSessions = new Dictionary<string, CharacterSession>();
            _undoStacks = new Dictionary<string, Stack<CustomizationHistory>>();
            _redoStacks = new Dictionary<string, Stack<CustomizationHistory>>();

            InitializeOptionCache();
            SubscribeToEvents();

            _logger.LogInformation("Customizer initialized with configuration: {@Config}", _config);
        }

        /// <summary>
        /// Create a new character with optional preset;
        /// </summary>
        public async Task<CharacterCustomization> CreateCharacterAsync(string characterId, string presetId = null)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogInformation("Creating new character: {CharacterId} with preset: {PresetId}",
                characterId, presetId ?? "none");

            try
            {
                CharacterCustomization customization;

                if (!string.IsNullOrEmpty(presetId))
                {
                    // Load from preset;
                    var preset = await _repository.GetPresetAsync(presetId);
                    if (preset == null)
                        throw new PresetNotFoundException($"Preset not found: {presetId}");

                    customization = new CharacterCustomization;
                    {
                        CharacterId = characterId,
                        PresetId = presetId,
                        Selections = preset.Selections.ToDictionary(
                            kvp => kvp.Key,
                            kvp => new CustomizationSelection;
                            {
                                OptionId = kvp.Value.OptionId,
                                Category = kvp.Value.Category,
                                Value = kvp.Value.Value,
                                BlendValue = kvp.Value.BlendValue,
                                IsActive = true;
                            }),
                        Version = 1;
                    };

                    _logger.LogDebug("Character created from preset: {PresetId}", presetId);
                }
                else;
                {
                    // Create default character;
                    customization = new CharacterCustomization;
                    {
                        CharacterId = characterId,
                        Version = 1;
                    };

                    // Apply default options for each category;
                    await ApplyDefaultOptionsAsync(customization);

                    _logger.LogDebug("Character created with default options");
                }

                // Initialize session;
                var session = new CharacterSession;
                {
                    CharacterId = characterId,
                    Customization = customization,
                    CreatedAt = DateTime.UtcNow,
                    LastActivity = DateTime.UtcNow;
                };

                lock (_sessionLock)
                {
                    _activeSessions[characterId] = session;
                    _undoStacks[characterId] = new Stack<CustomizationHistory>();
                    _redoStacks[characterId] = new Stack<CustomizationHistory>();
                }

                // Cache the customization;
                await CacheCustomizationAsync(customization);

                // Save to repository;
                await _repository.SaveCharacterAsync(customization);

                // Publish event;
                await _eventBus.PublishAsync(new CharacterCreatedEvent;
                {
                    CharacterId = characterId,
                    PresetId = presetId,
                    Timestamp = DateTime.UtcNow,
                    CustomizationCount = customization.Selections.Count;
                });

                // Update metrics;
                _metrics.IncrementCounter("characters.created", new Dictionary<string, string>
                {
                    ["has_preset"] = (!string.IsNullOrEmpty(presetId)).ToString()
                });

                _logger.LogInformation("Character created successfully: {CharacterId}", characterId);

                return customization;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to create character: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Load character customization;
        /// </summary>
        public async Task<CharacterCustomization> LoadCharacterAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogDebug("Loading character: {CharacterId}", characterId);

            try
            {
                // Check cache first;
                var cacheKey = $"{CACHE_PREFIX}{characterId}";
                if (_cache.TryGetValue(cacheKey, out CharacterCustomization cachedCustomization))
                {
                    _logger.LogDebug("Character loaded from cache: {CharacterId}", characterId);
                    return cachedCustomization;
                }

                // Load from repository;
                var customization = await _repository.LoadCharacterAsync(characterId);
                if (customization == null)
                    throw new CharacterNotFoundException($"Character not found: {characterId}");

                // Update session;
                lock (_sessionLock)
                {
                    if (_activeSessions.TryGetValue(characterId, out var existingSession))
                    {
                        existingSession.Customization = customization;
                        existingSession.LastActivity = DateTime.UtcNow;
                    }
                    else;
                    {
                        _activeSessions[characterId] = new CharacterSession;
                        {
                            CharacterId = characterId,
                            Customization = customization,
                            LastActivity = DateTime.UtcNow;
                        };

                        if (!_undoStacks.ContainsKey(characterId))
                            _undoStacks[characterId] = new Stack<CustomizationHistory>();

                        if (!_redoStacks.ContainsKey(characterId))
                            _redoStacks[characterId] = new Stack<CustomizationHistory>();
                    }
                }

                // Cache the result;
                await CacheCustomizationAsync(customization);

                // Update metrics;
                _metrics.IncrementCounter("characters.loaded");

                _logger.LogInformation("Character loaded successfully: {CharacterId}", characterId);

                return customization;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to load character: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save character customization;
        /// </summary>
        public async Task<bool> SaveCharacterAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogDebug("Saving character: {CharacterId}", characterId);

            try
            {
                CharacterCustomization customization;

                lock (_sessionLock)
                {
                    if (!_activeSessions.TryGetValue(characterId, out var session))
                        throw new CharacterNotFoundException($"Character session not found: {characterId}");

                    customization = session.Customization;
                    customization.UpdatedAt = DateTime.UtcNow;
                    customization.Version++;

                    session.LastActivity = DateTime.UtcNow;
                }

                // Save to repository;
                var success = await _repository.SaveCharacterAsync(customization);

                if (success)
                {
                    // Update cache;
                    await CacheCustomizationAsync(customization);

                    // Publish event;
                    await _eventBus.PublishAsync(new CharacterSavedEvent;
                    {
                        CharacterId = characterId,
                        Timestamp = DateTime.UtcNow,
                        Version = customization.Version;
                    });

                    // Update metrics;
                    _metrics.IncrementCounter("characters.saved");

                    _logger.LogInformation("Character saved successfully: {CharacterId} (Version: {Version})",
                        characterId, customization.Version);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to save character: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Delete character customization;
        /// </summary>
        public async Task<bool> DeleteCharacterAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogInformation("Deleting character: {CharacterId}", characterId);

            try
            {
                // Remove from cache;
                var cacheKey = $"{CACHE_PREFIX}{characterId}";
                _cache.Remove(cacheKey);

                // Remove from sessions;
                lock (_sessionLock)
                {
                    _activeSessions.Remove(characterId);
                    _undoStacks.Remove(characterId);
                    _redoStacks.Remove(characterId);
                }

                // Delete from repository;
                var success = await _repository.DeleteCharacterAsync(characterId);

                if (success)
                {
                    // Publish event;
                    await _eventBus.PublishAsync(new CharacterDeletedEvent;
                    {
                        CharacterId = characterId,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Update metrics;
                    _metrics.IncrementCounter("characters.deleted");

                    _logger.LogInformation("Character deleted successfully: {CharacterId}", characterId);
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to delete character: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Apply a single customization to character;
        /// </summary>
        public async Task<CustomizationResult> ApplyCustomizationAsync(string characterId, string optionId, object value)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(optionId))
                throw new ArgumentException("Option ID cannot be empty", nameof(optionId));

            _logger.LogDebug("Applying customization: {OptionId} = {Value} to character: {CharacterId}",
                optionId, value, characterId);

            try
            {
                // Load character;
                var customization = await LoadCharacterAsync(characterId);

                // Get option details;
                var option = await GetOptionAsync(optionId);
                if (option == null)
                    throw new OptionNotFoundException($"Option not found: {optionId}");

                // Validate the customization;
                var validationResult = await ValidateCustomizationAsync(characterId, optionId, value);
                if (!validationResult.IsValid)
                {
                    return new CustomizationResult;
                    {
                        Success = false,
                        ErrorMessage = validationResult.ErrorMessage,
                        ValidationErrors = validationResult.Errors;
                    };
                }

                // Save previous state for undo;
                await SaveToUndoStackAsync(characterId, optionId, option.Category);

                // Apply the customization;
                var selection = new CustomizationSelection;
                {
                    OptionId = optionId,
                    Category = option.Category,
                    Value = value,
                    BlendValue = 1.0f,
                    IsActive = true,
                    LayerOrder = GetNextLayerOrder(customization, option.Category)
                };

                customization.Selections[optionId] = selection;
                customization.UpdatedAt = DateTime.UtcNow;

                // Apply stat modifiers;
                await ApplyStatModifiersAsync(customization, option);

                // Clear redo stack since we made a new change;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Generate preview if configured;
                CustomizationPreview preview = null;
                if (_config.AutoGeneratePreview)
                {
                    preview = await GeneratePreviewAsync(characterId);
                }

                // Publish event;
                await _eventBus.PublishAsync(new CustomizationAppliedEvent;
                {
                    CharacterId = characterId,
                    OptionId = optionId,
                    Category = option.Category,
                    Value = value,
                    Timestamp = DateTime.UtcNow;
                });

                // Update metrics;
                _metrics.IncrementCounter("customizations.applied", new Dictionary<string, string>
                {
                    ["category"] = option.Category.ToString(),
                    ["option_type"] = option.Type.ToString()
                });

                _logger.LogInformation("Customization applied successfully: {OptionId} to character: {CharacterId}",
                    optionId, characterId);

                return new CustomizationResult;
                {
                    Success = true,
                    CharacterId = characterId,
                    OptionId = optionId,
                    AppliedValue = value,
                    Preview = preview,
                    UpdatedCustomization = customization;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply customization: {OptionId} to character: {CharacterId}",
                    optionId, characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to apply customization: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Apply multiple customizations in batch;
        /// </summary>
        public async Task<CustomizationResult> ApplyBatchCustomizationsAsync(string characterId, Dictionary<string, object> customizations)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (customizations == null || customizations.Count == 0)
                throw new ArgumentException("Customizations cannot be empty", nameof(customizations));

            _logger.LogDebug("Applying batch customizations ({Count}) to character: {CharacterId}",
                customizations.Count, characterId);

            try
            {
                // Save previous state for undo;
                await SaveBatchToUndoStackAsync(characterId, customizations.Keys.ToList());

                var results = new List<SingleCustomizationResult>();
                var customization = await LoadCharacterAsync(characterId);

                foreach (var kvp in customizations)
                {
                    var optionId = kvp.Key;
                    var value = kvp.Value;

                    try
                    {
                        // Get option details;
                        var option = await GetOptionAsync(optionId);
                        if (option == null)
                        {
                            results.Add(new SingleCustomizationResult;
                            {
                                OptionId = optionId,
                                Success = false,
                                ErrorMessage = $"Option not found: {optionId}"
                            });
                            continue;
                        }

                        // Validate;
                        var validationResult = await ValidateCustomizationAsync(characterId, optionId, value);
                        if (!validationResult.IsValid)
                        {
                            results.Add(new SingleCustomizationResult;
                            {
                                OptionId = optionId,
                                Success = false,
                                ErrorMessage = validationResult.ErrorMessage,
                                ValidationErrors = validationResult.Errors;
                            });
                            continue;
                        }

                        // Apply;
                        var selection = new CustomizationSelection;
                        {
                            OptionId = optionId,
                            Category = option.Category,
                            Value = value,
                            BlendValue = 1.0f,
                            IsActive = true,
                            LayerOrder = GetNextLayerOrder(customization, option.Category)
                        };

                        customization.Selections[optionId] = selection;

                        // Apply stat modifiers;
                        await ApplyStatModifiersAsync(customization, option);

                        results.Add(new SingleCustomizationResult;
                        {
                            OptionId = optionId,
                            Success = true,
                            AppliedValue = value;
                        });
                    }
                    catch (Exception ex)
                    {
                        results.Add(new SingleCustomizationResult;
                        {
                            OptionId = optionId,
                            Success = false,
                            ErrorMessage = $"Failed to apply: {ex.Message}"
                        });

                        _logger.LogWarning(ex, "Failed to apply customization in batch: {OptionId}", optionId);
                    }
                }

                customization.UpdatedAt = DateTime.UtcNow;

                // Clear redo stack;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Generate preview;
                CustomizationPreview preview = null;
                if (_config.AutoGeneratePreview)
                {
                    preview = await GeneratePreviewAsync(characterId);
                }

                // Publish event;
                await _eventBus.PublishAsync(new BatchCustomizationAppliedEvent;
                {
                    CharacterId = characterId,
                    AppliedCount = results.Count(r => r.Success),
                    FailedCount = results.Count(r => !r.Success),
                    Timestamp = DateTime.UtcNow;
                });

                // Update metrics;
                _metrics.IncrementCounter("customizations.batch_applied", new Dictionary<string, string>
                {
                    ["total"] = customizations.Count.ToString(),
                    ["success"] = results.Count(r => r.Success).ToString(),
                    ["failed"] = results.Count(r => !r.Success).ToString()
                });

                _logger.LogInformation("Batch customizations applied: {SuccessCount} successful, {FailedCount} failed",
                    results.Count(r => r.Success), results.Count(r => !r.Success));

                return new CustomizationResult;
                {
                    Success = results.All(r => r.Success),
                    CharacterId = characterId,
                    BatchResults = results,
                    Preview = preview,
                    UpdatedCustomization = customization;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply batch customizations to character: {CharacterId}", characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to apply batch customizations: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Reset a specific customization to default;
        /// </summary>
        public async Task<CustomizationResult> ResetCustomizationAsync(string characterId, string optionId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(optionId))
                throw new ArgumentException("Option ID cannot be empty", nameof(optionId));

            _logger.LogDebug("Resetting customization: {OptionId} for character: {CharacterId}",
                optionId, characterId);

            try
            {
                var customization = await LoadCharacterAsync(characterId);

                if (!customization.Selections.ContainsKey(optionId))
                {
                    return new CustomizationResult;
                    {
                        Success = false,
                        ErrorMessage = $"Customization not found: {optionId}"
                    };
                }

                // Save to undo stack;
                await SaveToUndoStackAsync(characterId, optionId, customization.Selections[optionId].Category);

                // Get default value;
                var option = await GetOptionAsync(optionId);
                var defaultValue = option?.DefaultValue ?? 0;

                // Reset to default;
                customization.Selections[optionId].Value = defaultValue;
                customization.Selections[optionId].BlendValue = 1.0f;
                customization.UpdatedAt = DateTime.UtcNow;

                // Clear redo stack;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Generate preview;
                CustomizationPreview preview = null;
                if (_config.AutoGeneratePreview)
                {
                    preview = await GeneratePreviewAsync(characterId);
                }

                // Publish event;
                await _eventBus.PublishAsync(new CustomizationResetEvent;
                {
                    CharacterId = characterId,
                    OptionId = optionId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Customization reset: {OptionId} for character: {CharacterId}",
                    optionId, characterId);

                return new CustomizationResult;
                {
                    Success = true,
                    CharacterId = characterId,
                    OptionId = optionId,
                    AppliedValue = defaultValue,
                    Preview = preview,
                    UpdatedCustomization = customization;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset customization: {OptionId} for character: {CharacterId}",
                    optionId, characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to reset customization: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Reset all customizations in a category;
        /// </summary>
        public async Task<CustomizationResult> ResetCategoryAsync(string characterId, CustomizationCategory category)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogDebug("Resetting category: {Category} for character: {CharacterId}",
                category, characterId);

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var optionsInCategory = customization.Selections;
                    .Where(kvp => kvp.Value.Category == category)
                    .ToList();

                if (optionsInCategory.Count == 0)
                {
                    return new CustomizationResult;
                    {
                        Success = true,
                        Message = $"No customizations found in category: {category}"
                    };
                }

                // Save to undo stack;
                await SaveCategoryToUndoStackAsync(characterId, category, optionsInCategory);

                // Reset each option;
                var resetOptions = new List<string>();

                foreach (var kvp in optionsInCategory)
                {
                    var option = await GetOptionAsync(kvp.Key);
                    if (option != null)
                    {
                        kvp.Value.Value = option.DefaultValue;
                        kvp.Value.BlendValue = 1.0f;
                        resetOptions.Add(kvp.Key);
                    }
                }

                customization.UpdatedAt = DateTime.UtcNow;

                // Clear redo stack;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Generate preview;
                CustomizationPreview preview = null;
                if (_config.AutoGeneratePreview)
                {
                    preview = await GeneratePreviewAsync(characterId);
                }

                // Publish event;
                await _eventBus.PublishAsync(new CategoryResetEvent;
                {
                    CharacterId = characterId,
                    Category = category,
                    ResetCount = resetOptions.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Category reset: {Category} ({Count} options) for character: {CharacterId}",
                    category, resetOptions.Count, characterId);

                return new CustomizationResult;
                {
                    Success = true,
                    CharacterId = characterId,
                    ResetOptions = resetOptions,
                    Preview = preview,
                    UpdatedCustomization = customization;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset category: {Category} for character: {CharacterId}",
                    category, characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to reset category: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Reset all customizations;
        /// </summary>
        public async Task<CustomizationResult> ResetAllCustomizationsAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogDebug("Resetting all customizations for character: {CharacterId}", characterId);

            try
            {
                var customization = await LoadCharacterAsync(characterId);

                if (customization.Selections.Count == 0)
                {
                    return new CustomizationResult;
                    {
                        Success = true,
                        Message = "No customizations to reset"
                    };
                }

                // Save to undo stack;
                await SaveAllToUndoStackAsync(characterId, customization.Selections);

                // Reset all options to defaults;
                var resetOptions = new List<string>();

                foreach (var kvp in customization.Selections)
                {
                    var option = await GetOptionAsync(kvp.Key);
                    if (option != null)
                    {
                        kvp.Value.Value = option.DefaultValue;
                        kvp.Value.BlendValue = 1.0f;
                        resetOptions.Add(kvp.Key);
                    }
                }

                customization.UpdatedAt = DateTime.UtcNow;

                // Clear redo stack;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Generate preview;
                CustomizationPreview preview = null;
                if (_config.AutoGeneratePreview)
                {
                    preview = await GeneratePreviewAsync(characterId);
                }

                // Publish event;
                await _eventBus.PublishAsync(new AllCustomizationsResetEvent;
                {
                    CharacterId = characterId,
                    ResetCount = resetOptions.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("All customizations reset ({Count} options) for character: {CharacterId}",
                    resetOptions.Count, characterId);

                return new CustomizationResult;
                {
                    Success = true,
                    CharacterId = characterId,
                    ResetOptions = resetOptions,
                    Preview = preview,
                    UpdatedCustomization = customization;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reset all customizations for character: {CharacterId}", characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to reset all customizations: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Get available customization options for character;
        /// </summary>
        public async Task<List<CustomizationOption>> GetAvailableOptionsAsync(string characterId, CustomizationCategory? category = null)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var allOptions = await _optionProvider.GetAllOptionsAsync();

                // Filter by category if specified;
                var filteredOptions = category.HasValue;
                    ? allOptions.Where(o => o.Category == category.Value).ToList()
                    : allOptions;

                // Filter based on character's current state, requirements, etc.
                var availableOptions = new List<CustomizationOption>();

                foreach (var option in filteredOptions)
                {
                    if (await IsOptionAvailableAsync(option, customization))
                    {
                        availableOptions.Add(option);
                    }
                }

                _logger.LogDebug("Retrieved {Count} available options for character: {CharacterId}",
                    availableOptions.Count, characterId);

                return availableOptions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available options for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to get available options: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get option details;
        /// </summary>
        public async Task<CustomizationOption> GetOptionAsync(string optionId)
        {
            if (string.IsNullOrWhiteSpace(optionId))
                throw new ArgumentException("Option ID cannot be empty", nameof(optionId));

            try
            {
                var cacheKey = $"option_{optionId}";

                if (_cache.TryGetValue(cacheKey, out CustomizationOption cachedOption))
                {
                    return cachedOption;
                }

                var option = await _optionProvider.GetOptionAsync(optionId);
                if (option == null)
                    return null;

                // Cache the option;
                _cache.Set(cacheKey, option, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));

                return option;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get option: {OptionId}", optionId);
                throw new CustomizerException($"Failed to get option: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Search for options;
        /// </summary>
        public async Task<List<CustomizationOption>> SearchOptionsAsync(string query, CustomizationCategory? category = null)
        {
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("Search query cannot be empty", nameof(query));

            try
            {
                var allOptions = await _optionProvider.GetAllOptionsAsync();

                var filteredOptions = category.HasValue;
                    ? allOptions.Where(o => o.Category == category.Value)
                    : allOptions;

                var searchResults = filteredOptions;
                    .Where(o => o.OptionName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                               o.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
                               o.Category.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(o => o.OptionName)
                    .ToList();

                _logger.LogDebug("Search for '{Query}' returned {Count} results", query, searchResults.Count);

                return searchResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search options with query: {Query}", query);
                throw new CustomizerException($"Failed to search options: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Validate customization before applying;
        /// </summary>
        public async Task<ValidationResult> ValidateCustomizationAsync(string characterId, string optionId, object value)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(optionId))
                throw new ArgumentException("Option ID cannot be empty", nameof(optionId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var option = await GetOptionAsync(optionId);

                if (option == null)
                {
                    return new ValidationResult;
                    {
                        IsValid = false,
                        Errors = new List<string> { $"Option not found: {optionId}" }
                    };
                }

                return await _validationEngine.ValidateAsync(customization, option, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate customization: {OptionId} for character: {CharacterId}",
                    optionId, characterId);

                return new ValidationResult;
                {
                    IsValid = false,
                    Errors = new List<string> { $"Validation error: {ex.Message}" }
                };
            }
        }

        /// <summary>
        /// Generate preview of character customization;
        /// </summary>
        public async Task<CustomizationPreview> GeneratePreviewAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var preview = await _previewGenerator.GenerateAsync(customization);

                _logger.LogDebug("Generated preview for character: {CharacterId}", characterId);

                return preview;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate preview for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to generate preview: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generate visualization data for character;
        /// </summary>
        public async Task<VisualizationData> GenerateVisualizationAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);

                var visualization = new VisualizationData;
                {
                    CharacterId = characterId,
                    GeneratedAt = DateTime.UtcNow,
                    MeshData = await GenerateMeshDataAsync(customization),
                    TextureData = await GenerateTextureDataAsync(customization),
                    MaterialData = await GenerateMaterialDataAsync(customization),
                    SkeletonData = await GenerateSkeletonDataAsync(customization),
                    BlendShapes = await _blendShapeEngine.GenerateBlendShapesAsync(customization)
                };

                _logger.LogDebug("Generated visualization for character: {CharacterId}", characterId);

                return visualization;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate visualization for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to generate visualization: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Compare two character customizations;
        /// </summary>
        public async Task<ComparisonResult> CompareCustomizationsAsync(string characterId, string otherCharacterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(otherCharacterId))
                throw new ArgumentException("Other character ID cannot be empty", nameof(otherCharacterId));

            try
            {
                var customization1 = await LoadCharacterAsync(characterId);
                var customization2 = await LoadCharacterAsync(otherCharacterId);

                var comparison = new ComparisonResult;
                {
                    CharacterId1 = characterId,
                    CharacterId2 = otherCharacterId,
                    ComparedAt = DateTime.UtcNow,
                    Differences = new List<CustomizationDifference>()
                };

                // Find common options;
                var allOptionIds = customization1.Selections.Keys;
                    .Union(customization2.Selections.Keys)
                    .Distinct();

                foreach (var optionId in allOptionIds)
                {
                    var has1 = customization1.Selections.TryGetValue(optionId, out var selection1);
                    var has2 = customization2.Selections.TryGetValue(optionId, out var selection2);

                    if (has1 != has2)
                    {
                        comparison.Differences.Add(new CustomizationDifference;
                        {
                            OptionId = optionId,
                            DifferenceType = has1 ? DifferenceType.MissingInSecond : DifferenceType.MissingInFirst;
                        });
                    }
                    else if (selection1.Value != selection2.Value)
                    {
                        comparison.Differences.Add(new CustomizationDifference;
                        {
                            OptionId = optionId,
                            DifferenceType = DifferenceType.ValueDifference,
                            Value1 = selection1.Value,
                            Value2 = selection2.Value;
                        });
                    }
                }

                comparison.SimilarityScore = CalculateSimilarityScore(comparison.Differences);

                _logger.LogDebug("Compared customizations: {CharacterId} vs {OtherCharacterId} - Similarity: {Score}%",
                    characterId, otherCharacterId, comparison.SimilarityScore);

                return comparison;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to compare customizations: {CharacterId} vs {OtherCharacterId}",
                    characterId, otherCharacterId);
                throw new CustomizerException($"Failed to compare customizations: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Save current customization as a preset;
        /// </summary>
        public async Task<string> SaveAsPresetAsync(string characterId, string presetName)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(presetName))
                throw new ArgumentException("Preset name cannot be empty", nameof(presetName));

            try
            {
                var customization = await LoadCharacterAsync(characterId);

                var preset = new CustomizationPreset;
                {
                    PresetId = Guid.NewGuid().ToString(),
                    PresetName = presetName,
                    CharacterId = characterId,
                    Selections = customization.Selections.ToDictionary(
                        kvp => kvp.Key,
                        kvp => new CustomizationSelection;
                        {
                            OptionId = kvp.Value.OptionId,
                            Category = kvp.Value.Category,
                            Value = kvp.Value.Value,
                            BlendValue = kvp.Value.BlendValue;
                        }),
                    CreatedAt = DateTime.UtcNow,
                    UsageCount = 0;
                };

                var presetId = await _repository.SavePresetAsync(preset);

                _logger.LogInformation("Saved preset: {PresetName} ({PresetId}) from character: {CharacterId}",
                    presetName, presetId, characterId);

                return presetId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save preset for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to save preset: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Apply a preset to character;
        /// </summary>
        public async Task<bool> ApplyPresetAsync(string characterId, string presetId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(presetId))
                throw new ArgumentException("Preset ID cannot be empty", nameof(presetId));

            try
            {
                var preset = await _repository.GetPresetAsync(presetId);
                if (preset == null)
                    throw new PresetNotFoundException($"Preset not found: {presetId}");

                var customization = await LoadCharacterAsync(characterId);

                // Save current state to undo stack;
                await SaveAllToUndoStackAsync(characterId, customization.Selections);

                // Apply preset selections;
                foreach (var kvp in preset.Selections)
                {
                    customization.Selections[kvp.Key] = new CustomizationSelection;
                    {
                        OptionId = kvp.Value.OptionId,
                        Category = kvp.Value.Category,
                        Value = kvp.Value.Value,
                        BlendValue = kvp.Value.BlendValue,
                        IsActive = true;
                    };
                }

                customization.PresetId = presetId;
                customization.UpdatedAt = DateTime.UtcNow;

                // Clear redo stack;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Update preset usage count;
                preset.UsageCount++;
                await _repository.SavePresetAsync(preset);

                // Publish event;
                await _eventBus.PublishAsync(new PresetAppliedEvent;
                {
                    CharacterId = characterId,
                    PresetId = presetId,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Applied preset: {PresetId} to character: {CharacterId}",
                    presetId, characterId);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply preset: {PresetId} to character: {CharacterId}",
                    presetId, characterId);
                throw new CustomizerException($"Failed to apply preset: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get available presets;
        /// </summary>
        public async Task<List<CustomizationPreset>> GetAvailablePresetsAsync()
        {
            try
            {
                var presets = await _repository.GetAllPresetsAsync();
                return presets.OrderByDescending(p => p.UsageCount).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get available presets");
                throw new CustomizerException($"Failed to get presets: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Randomize character customizations;
        /// </summary>
        public async Task<CustomizationResult> RandomizeAsync(string characterId, CustomizationCategory? category = null)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            _logger.LogDebug("Randomizing customizations for character: {CharacterId}", characterId);

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var options = await GetAvailableOptionsAsync(characterId, category);

                if (options.Count == 0)
                {
                    return new CustomizationResult;
                    {
                        Success = false,
                        ErrorMessage = "No options available for randomization"
                    };
                }

                // Save current state to undo stack;
                var optionsToRandomize = category.HasValue;
                    ? customization.Selections.Where(kvp => kvp.Value.Category == category.Value).ToList()
                    : customization.Selections.ToList();

                await SaveBatchToUndoStackAsync(characterId, optionsToRandomize.Select(kvp => kvp.Key).ToList());

                var random = new Random();
                var randomizedOptions = new List<string>();

                foreach (var option in options)
                {
                    if (option.Type == OptionType.Slider || option.Type == OptionType.Range)
                    {
                        var randomValue = random.NextSingle() * (option.MaxValue - option.MinValue) + option.MinValue;
                        randomValue = (float)Math.Round(randomValue / option.StepSize) * option.StepSize;

                        if (customization.Selections.ContainsKey(option.OptionId))
                        {
                            customization.Selections[option.OptionId].Value = randomValue;
                        }
                        else;
                        {
                            customization.Selections[option.OptionId] = new CustomizationSelection;
                            {
                                OptionId = option.OptionId,
                                Category = option.Category,
                                Value = randomValue,
                                BlendValue = 1.0f,
                                IsActive = true;
                            };
                        }

                        randomizedOptions.Add(option.OptionId);
                    }
                    else if (option.Type == OptionType.Color)
                    {
                        var randomColor = $"#{random.Next(0x1000000):X6}";
                        customization.Selections[option.OptionId] = new CustomizationSelection;
                        {
                            OptionId = option.OptionId,
                            Category = option.Category,
                            Value = randomColor,
                            BlendValue = 1.0f,
                            IsActive = true;
                        };

                        randomizedOptions.Add(option.OptionId);
                    }
                }

                customization.UpdatedAt = DateTime.UtcNow;

                // Clear redo stack;
                ClearRedoStack(characterId);

                // Save changes;
                await SaveCharacterAsync(characterId);

                // Generate preview;
                CustomizationPreview preview = null;
                if (_config.AutoGeneratePreview)
                {
                    preview = await GeneratePreviewAsync(characterId);
                }

                // Publish event;
                await _eventBus.PublishAsync(new CharacterRandomizedEvent;
                {
                    CharacterId = characterId,
                    Category = category,
                    RandomizedCount = randomizedOptions.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation("Randomized {Count} options for character: {CharacterId}",
                    randomizedOptions.Count, characterId);

                return new CustomizationResult;
                {
                    Success = true,
                    CharacterId = characterId,
                    RandomizedOptions = randomizedOptions,
                    Preview = preview,
                    UpdatedCustomization = customization;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to randomize character: {CharacterId}", characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to randomize: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Make customization symmetrical;
        /// </summary>
        public async Task<CustomizationResult> SymmetrizeAsync(string characterId, string optionId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (string.IsNullOrWhiteSpace(optionId))
                throw new ArgumentException("Option ID cannot be empty", nameof(optionId));

            try
            {
                // This would require knowing which options are left/right pairs;
                // Implementation would find the counterpart option and set it to the same value;

                _logger.LogDebug("Symmetrize functionality for {OptionId} on character: {CharacterId}",
                    optionId, characterId);

                // TODO: Implement based on option naming conventions or metadata;

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = "Symmetrize not implemented for this option"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to symmetrize option: {OptionId} for character: {CharacterId}",
                    optionId, characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to symmetrize: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Mirror customizations across category;
        /// </summary>
        public async Task<CustomizationResult> MirrorAsync(string characterId, CustomizationCategory category)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var categoryOptions = customization.Selections;
                    .Where(kvp => kvp.Value.Category == category)
                    .ToList();

                // TODO: Implement mirroring logic based on option relationships;

                _logger.LogDebug("Mirror functionality for category: {Category} on character: {CharacterId}",
                    category, characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = "Mirror not implemented for this category"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to mirror category: {Category} for character: {CharacterId}",
                    category, characterId);

                return new CustomizationResult;
                {
                    Success = false,
                    ErrorMessage = $"Failed to mirror: {ex.Message}",
                    Exception = ex;
                };
            }
        }

        /// <summary>
        /// Generate genetic variations of character;
        /// </summary>
        public async Task<GeneticResult> GenerateGeneticVariationsAsync(string characterId, int count)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            if (count <= 0 || count > 100)
                throw new ArgumentException("Count must be between 1 and 100", nameof(count));

            try
            {
                var baseCustomization = await LoadCharacterAsync(characterId);
                var variations = new List<CharacterCustomization>();

                for (int i = 0; i < count; i++)
                {
                    var variation = baseCustomization.DeepClone();
                    variation.CharacterId = $"{characterId}_variation_{i}";

                    // Apply genetic algorithm to create variations;
                    await ApplyGeneticVariationAsync(variation);

                    variations.Add(variation);
                }

                _logger.LogInformation("Generated {Count} genetic variations for character: {CharacterId}",
                    count, characterId);

                return new GeneticResult;
                {
                    Success = true,
                    BaseCharacterId = characterId,
                    Variations = variations,
                    VariationCount = count;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate genetic variations for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to generate genetic variations: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get customization statistics;
        /// </summary>
        public async Task<CustomizationStatistics> GetStatisticsAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);

                var stats = new CustomizationStatistics;
                {
                    CharacterId = characterId,
                    TotalCustomizations = customization.Selections.Count,
                    CustomizationsByCategory = customization.Selections;
                        .GroupBy(kvp => kvp.Value.Category)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    LastUpdated = customization.UpdatedAt,
                    Version = customization.Version,
                    ActiveSelections = customization.Selections.Count(kvp => kvp.Value.IsActive),
                    AverageBlendValue = customization.Selections.Any()
                        ? customization.Selections.Average(kvp => kvp.Value.BlendValue)
                        : 0;
                };

                return stats;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get statistics for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to get statistics: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Get customization history;
        /// </summary>
        public async Task<List<CustomizationHistory>> GetHistoryAsync(string characterId, int limit = 50)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                lock (_sessionLock)
                {
                    if (_undoStacks.TryGetValue(characterId, out var undoStack))
                    {
                        return undoStack.Take(limit).Reverse().ToList();
                    }
                }

                return new List<CustomizationHistory>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get history for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to get history: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Undo last customization change;
        /// </summary>
        public async Task<bool> UndoLastChangeAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                lock (_sessionLock)
                {
                    if (!_undoStacks.TryGetValue(characterId, out var undoStack) || undoStack.Count == 0)
                        return false;

                    if (!_redoStacks.TryGetValue(characterId, out var redoStack))
                    {
                        redoStack = new Stack<CustomizationHistory>();
                        _redoStacks[characterId] = redoStack;
                    }

                    var lastChange = undoStack.Pop();
                    redoStack.Push(lastChange);

                    // Apply undo logic;
                    // This would restore the previous state;

                    _logger.LogDebug("Undo performed for character: {CharacterId}", characterId);

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to undo for character: {CharacterId}", characterId);
                return false;
            }
        }

        /// <summary>
        /// Redo last undone change;
        /// </summary>
        public async Task<bool> RedoLastChangeAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                lock (_sessionLock)
                {
                    if (!_redoStacks.TryGetValue(characterId, out var redoStack) || redoStack.Count == 0)
                        return false;

                    if (!_undoStacks.TryGetValue(characterId, out var undoStack))
                    {
                        undoStack = new Stack<CustomizationHistory>();
                        _undoStacks[characterId] = undoStack;
                    }

                    var lastRedo = redoStack.Pop();
                    undoStack.Push(lastRedo);

                    // Apply redo logic;

                    _logger.LogDebug("Redo performed for character: {CharacterId}", characterId);

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to redo for character: {CharacterId}", characterId);
                return false;
            }
        }

        /// <summary>
        /// Generate morph targets for character;
        /// </summary>
        public async Task<MorphTargetData> GenerateMorphTargetsAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var morphTargets = await _blendShapeEngine.GenerateBlendShapesAsync(customization);

                _logger.LogDebug("Generated morph targets for character: {CharacterId}", characterId);

                return morphTargets;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate morph targets for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to generate morph targets: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Generate outfit configuration;
        /// </summary>
        public async Task<OutfitConfiguration> GenerateOutfitConfigurationAsync(string characterId)
        {
            if (string.IsNullOrWhiteSpace(characterId))
                throw new ArgumentException("Character ID cannot be empty", nameof(characterId));

            try
            {
                var customization = await LoadCharacterAsync(characterId);
                var outfitConfig = await _outfitManager.GenerateConfigurationAsync(customization);

                _logger.LogDebug("Generated outfit configuration for character: {CharacterId}", characterId);

                return outfitConfig;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate outfit configuration for character: {CharacterId}", characterId);
                throw new CustomizerException($"Failed to generate outfit configuration: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Dispose resources;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Clean up active sessions;
                lock (_sessionLock)
                {
                    _activeSessions.Clear();
                    _undoStacks.Clear();
                    _redoStacks.Clear();
                }

                _logger.LogInformation("Customizer disposed");
            }
        }

        #region Private Methods;

        private void InitializeOptionCache()
        {
            // Pre-load frequently used options;
            Task.Run(async () =>
            {
                try
                {
                    var commonOptions = await _optionProvider.GetCommonOptionsAsync();
                    foreach (var option in commonOptions)
                    {
                        var cacheKey = $"option_{option.OptionId}";
                        _cache.Set(cacheKey, option, TimeSpan.FromHours(1));
                    }

                    _logger.LogDebug("Pre-loaded {Count} common options into cache", commonOptions.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to pre-load common options");
                }
            });
        }

        private void SubscribeToEvents()
        {
            _eventBus.Subscribe<OptionUpdatedEvent>(async e =>
            {
                await OnOptionUpdatedAsync(e);
            });

            _eventBus.Subscribe<CategoryUpdatedEvent>(async e =>
            {
                await OnCategoryUpdatedAsync(e);
            });
        }

        private async Task OnOptionUpdatedAsync(OptionUpdatedEvent e)
        {
            // Clear option from cache;
            var cacheKey = $"option_{e.OptionId}";
            _cache.Remove(cacheKey);

            _logger.LogDebug("Option cache cleared: {OptionId}", e.OptionId);

            // Notify active sessions that use this option;
            var affectedCharacters = new List<string>();

            lock (_sessionLock)
            {
                foreach (var session in _activeSessions.Values)
                {
                    if (session.Customization.Selections.ContainsKey(e.OptionId))
                    {
                        affectedCharacters.Add(session.CharacterId);
                    }
                }
            }

            if (affectedCharacters.Count > 0)
            {
                await _eventBus.PublishAsync(new OptionChangeAffectedCharactersEvent;
                {
                    OptionId = e.OptionId,
                    AffectedCharacterIds = affectedCharacters,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private async Task OnCategoryUpdatedAsync(CategoryUpdatedEvent e)
        {
            // Clear all options in category from cache;
            var options = await _optionProvider.GetOptionsByCategoryAsync(e.Category);
            foreach (var option in options)
            {
                var cacheKey = $"option_{option.OptionId}";
                _cache.Remove(cacheKey);
            }

            _logger.LogDebug("Category cache cleared: {Category} ({Count} options)",
                e.Category, options.Count);
        }

        private async Task ApplyDefaultOptionsAsync(CharacterCustomization customization)
        {
            var defaultOptions = await _optionProvider.GetDefaultOptionsAsync();

            foreach (var option in defaultOptions)
            {
                customization.Selections[option.OptionId] = new CustomizationSelection;
                {
                    OptionId = option.OptionId,
                    Category = option.Category,
                    Value = option.DefaultValue,
                    BlendValue = 1.0f,
                    IsActive = true;
                };
            }
        }

        private async Task<bool> IsOptionAvailableAsync(CustomizationOption option, CharacterCustomization customization)
        {
            // Check level requirement;
            if (option.UnlockLevel > 0)
            {
                // TODO: Get character level from metadata;
                var characterLevel = customization.Metadata.TryGetValue("Level", out var level)
                    ? Convert.ToInt32(level)
                    : 1;

                if (characterLevel < option.UnlockLevel)
                    return false;
            }

            // Check requirements;
            if (option.Requirements.Count > 0)
            {
                foreach (var requirement in option.Requirements)
                {
                    // TODO: Implement requirement checking;
                    // This would check if character meets the requirement;
                }
            }

            // Check compatibility with current selections;
            if (option.Compatibility.Count > 0)
            {
                foreach (var incompatibleOption in option.Compatibility)
                {
                    if (customization.Selections.ContainsKey(incompatibleOption))
                        return false;
                }
            }

            return true;
        }

        private async Task ApplyStatModifiersAsync(CharacterCustomization customization, CustomizationOption option)
        {
            if (option.StatModifiers.Count == 0)
                return;

            foreach (var modifier in option.StatModifiers)
            {
                var appliedModifier = new AppliedModifier;
                {
                    OptionId = option.OptionId,
                    StatName = modifier.StatName,
                    ModifierType = modifier.ModifierType,
                    Value = modifier.Value,
                    AppliedAt = DateTime.UtcNow;
                };

                customization.AppliedModifiers.Add(appliedModifier);
            }

            // Update character stats;
            await UpdateCharacterStatsAsync(customization);
        }

        private async Task UpdateCharacterStatsAsync(CharacterCustomization customization)
        {
            // Calculate total stats from all applied modifiers;
            var stats = new Dictionary<string, float>();

            foreach (var modifier in customization.AppliedModifiers)
            {
                if (!stats.ContainsKey(modifier.StatName))
                    stats[modifier.StatName] = 0;

                stats[modifier.StatName] += modifier.Value;
            }

            // Store in metadata;
            customization.Metadata["Stats"] = stats;
        }

        private int GetNextLayerOrder(CharacterCustomization customization, CustomizationCategory category)
        {
            var maxOrder = customization.Selections.Values;
                .Where(s => s.Category == category)
                .Select(s => s.LayerOrder)
                .DefaultIfEmpty(0)
                .Max();

            return maxOrder + 1;
        }

        private async Task CacheCustomizationAsync(CharacterCustomization customization)
        {
            var cacheKey = $"{CACHE_PREFIX}{customization.CharacterId}";
            _cache.Set(cacheKey, customization, TimeSpan.FromMinutes(CACHE_DURATION_MINUTES));
        }

        private async Task SaveToUndoStackAsync(string characterId, string optionId, CustomizationCategory category)
        {
            lock (_sessionLock)
            {
                if (!_undoStacks.TryGetValue(characterId, out var undoStack))
                {
                    undoStack = new Stack<CustomizationHistory>();
                    _undoStacks[characterId] = undoStack;
                }

                var history = new CustomizationHistory;
                {
                    CharacterId = characterId,
                    OptionId = optionId,
                    Category = category,
                    Action = "Apply",
                    Timestamp = DateTime.UtcNow;
                };

                undoStack.Push(history);

                // Limit stack size;
                while (undoStack.Count > MAX_UNDO_STEPS)
                {
                    undoStack.Pop();
                }
            }
        }

        private async Task SaveBatchToUndoStackAsync(string characterId, List<string> optionIds)
        {
            lock (_sessionLock)
            {
                if (!_undoStacks.TryGetValue(characterId, out var undoStack))
                {
                    undoStack = new Stack<CustomizationHistory>();
                    _undoStacks[characterId] = undoStack;
                }

                var history = new CustomizationHistory;
                {
                    CharacterId = characterId,
                    OptionIds = optionIds,
                    Action = "BatchApply",
                    Timestamp = DateTime.UtcNow;
                };

                undoStack.Push(history);

                while (undoStack.Count > MAX_UNDO_STEPS)
                {
                    undoStack.Pop();
                }
            }
        }

        private async Task SaveCategoryToUndoStackAsync(string characterId, CustomizationCategory category, List<KeyValuePair<string, CustomizationSelection>> options)
        {
            lock (_sessionLock)
            {
                if (!_undoStacks.TryGetValue(characterId, out var undoStack))
                {
                    undoStack = new Stack<CustomizationHistory>();
                    _undoStacks[characterId] = undoStack;
                }

                var history = new CustomizationHistory;
                {
                    CharacterId = characterId,
                    Category = category,
                    OptionIds = options.Select(kvp => kvp.Key).ToList(),
                    Action = "CategoryReset",
                    Timestamp = DateTime.UtcNow;
                };

                undoStack.Push(history);

                while (undoStack.Count > MAX_UNDO_STEPS)
                {
                    undoStack.Pop();
                }
            }
        }

        private async Task SaveAllToUndoStackAsync(string characterId, Dictionary<string, CustomizationSelection> selections)
        {
            lock (_sessionLock)
            {
                if (!_undoStacks.TryGetValue(characterId, out var undoStack))
                {
                    undoStack = new Stack<CustomizationHistory>();
                    _undoStacks[characterId] = undoStack;
                }

                var history = new CustomizationHistory;
                {
                    CharacterId = characterId,
                    OptionIds = selections.Keys.ToList(),
                    Action = "AllReset",
                    Timestamp = DateTime.UtcNow;
                };

                undoStack.Push(history);

                while (undoStack.Count > MAX_UNDO_STEPS)
                {
                    undoStack.Pop();
                }
            }
        }

        private void ClearRedoStack(string characterId)
        {
            lock (_sessionLock)
            {
                if (_redoStacks.TryGetValue(characterId, out var redoStack))
                {
                    redoStack.Clear();
                }
            }
        }

        private float CalculateSimilarityScore(List<CustomizationDifference> differences)
        {
            if (differences.Count == 0)
                return 100.0f;

            // Simple similarity calculation;
            // More sophisticated algorithms could be implemented;
            var totalDifferences = differences.Count;
            var matchingValues = differences.Count(d => d.DifferenceType == DifferenceType.ValueDifference);

            return 100.0f - (matchingValues * 10.0f / totalDifferences);
        }

        private async Task<MeshData> GenerateMeshDataAsync(CharacterCustomization customization)
        {
            // Generate mesh data based on customizations;
            // This would typically interface with 3D modeling tools;
            return new MeshData();
        }

        private async Task<TextureData> GenerateTextureDataAsync(CharacterCustomization customization)
        {
            // Generate texture data based on customizations;
            return new TextureData();
        }

        private async Task<MaterialData> GenerateMaterialDataAsync(CharacterCustomization customization)
        {
            // Generate material data based on customizations;
            return new MaterialData();
        }

        private async Task<SkeletonData> GenerateSkeletonDataAsync(CharacterCustomization customization)
        {
            // Generate skeleton data based on customizations;
            return new SkeletonData();
        }

        private async Task ApplyGeneticVariationAsync(CharacterCustomization variation)
        {
            // Apply genetic algorithm to create variation;
            // This would modify values based on genetic rules;
            var random = new Random();

            foreach (var selection in variation.Selections.Values)
            {
                // Small random variation;
                if (selection.Value is float floatValue)
                {
                    var variationAmount = (random.NextSingle() - 0.5f) * 0.1f; // ±5% variation;
                    selection.Value = floatValue + variationAmount;
                }
            }
        }

        #endregion;

        #region Supporting Classes;

        public enum CustomizationCategory;
        {
            Face,
            Hair,
            Eyes,
            Skin,
            Body,
            Clothing,
            Accessories,
            Tattoos,
            Scars,
            Makeup,
            Voice,
            Animation,
            Stats,
            SpecialAbilities,
            Custom;
        }

        public enum OptionType;
        {
            Slider,
            Color,
            Toggle,
            Dropdown,
            Texture,
            MorphTarget,
            BoneAdjustment,
            Material,
            Range,
            Preset;
        }

        public enum ModifierType;
        {
            Additive,
            Multiplicative,
            Override;
        }

        public enum DifferenceType;
        {
            MissingInFirst,
            MissingInSecond,
            ValueDifference,
            CategoryDifference;
        }

        public class CharacterSession;
        {
            public string CharacterId { get; set; }
            public CharacterCustomization Customization { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime LastActivity { get; set; }
            public int ChangeCount { get; set; }
        }

        public class CustomizerConfig;
        {
            public bool AutoGeneratePreview { get; set; } = true;
            public bool EnableUndoRedo { get; set; } = true;
            public bool CacheOptions { get; set; } = true;
            public int MaxUndoSteps { get; set; } = 50;
            public int CacheDurationMinutes { get; set; } = 30;
            public bool ValidateOnApply { get; set; } = true;
            public bool AutoSaveChanges { get; set; } = true;
            public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(1);
            public int MaxActiveSessions { get; set; } = 1000;
        }

        public class VisualData;
        {
            public string PreviewImageUrl { get; set; }
            public string ThumbnailUrl { get; set; }
            public string ModelPreviewUrl { get; set; }
            public Dictionary<string, object> VisualizationParameters { get; set; }

            public VisualData()
            {
                VisualizationParameters = new Dictionary<string, object>();
            }
        }

        public class StatModifier;
        {
            public string StatName { get; set; }
            public ModifierType ModifierType { get; set; }
            public float Value { get; set; }
            public string Description { get; set; }
        }

        public class Requirement;
        {
            public string Type { get; set; }
            public string Target { get; set; }
            public object RequiredValue { get; set; }
            public string Operator { get; set; }
        }

        public class Cost;
        {
            public int Gold { get; set; }
            public int Gems { get; set; }
            public int Experience { get; set; }
            public Dictionary<string, int> Items { get; set; }

            public Cost()
            {
                Items = new Dictionary<string, int>();
            }
        }

        public class AppliedModifier;
        {
            public string OptionId { get; set; }
            public string StatName { get; set; }
            public ModifierType ModifierType { get; set; }
            public float Value { get; set; }
            public DateTime AppliedAt { get; set; }
        }

        public class CustomizationResult;
        {
            public bool Success { get; set; }
            public string CharacterId { get; set; }
            public string OptionId { get; set; }
            public object AppliedValue { get; set; }
            public string ErrorMessage { get; set; }
            public Exception Exception { get; set; }
            public List<string> ValidationErrors { get; set; }
            public List<SingleCustomizationResult> BatchResults { get; set; }
            public List<string> ResetOptions { get; set; }
            public List<string> RandomizedOptions { get; set; }
            public CustomizationPreview Preview { get; set; }
            public CharacterCustomization UpdatedCustomization { get; set; }
            public string Message { get; set; }

            public CustomizationResult()
            {
                ValidationErrors = new List<string>();
                BatchResults = new List<SingleCustomizationResult>();
                ResetOptions = new List<string>();
                RandomizedOptions = new List<string>();
            }
        }

        public class SingleCustomizationResult;
        {
            public string OptionId { get; set; }
            public bool Success { get; set; }
            public object AppliedValue { get; set; }
            public string ErrorMessage { get; set; }
            public List<string> ValidationErrors { get; set; }

            public SingleCustomizationResult()
            {
                ValidationErrors = new List<string>();
            }
        }

        public class ValidationResult;
        {
            public bool IsValid { get; set; }
            public List<string> Errors { get; set; }
            public string ErrorMessage => Errors != null && Errors.Count > 0;
                ? string.Join("; ", Errors)
                : string.Empty;

            public ValidationResult()
            {
                Errors = new List<string>();
            }
        }

        public class CustomizationPreview;
        {
            public string CharacterId { get; set; }
            public string PreviewId { get; set; }
            public byte[] ImageData { get; set; }
            public string ImageUrl { get; set; }
            public DateTime GeneratedAt { get; set; }
            public Dictionary<string, object> Metadata { get; set; }

            public CustomizationPreview()
            {
                Metadata = new Dictionary<string, object>();
            }
        }

        public class VisualizationData;
        {
            public string CharacterId { get; set; }
            public DateTime GeneratedAt { get; set; }
            public MeshData MeshData { get; set; }
            public TextureData TextureData { get; set; }
            public MaterialData MaterialData { get; set; }
            public SkeletonData SkeletonData { get; set; }
            public MorphTargetData BlendShapes { get; set; }

            public VisualizationData()
            {
                MeshData = new MeshData();
                TextureData = new TextureData();
                MaterialData = new MaterialData();
                SkeletonData = new SkeletonData();
            }
        }

        public class ComparisonResult;
        {
            public string CharacterId1 { get; set; }
            public string CharacterId2 { get; set; }
            public DateTime ComparedAt { get; set; }
            public List<CustomizationDifference> Differences { get; set; }
            public float SimilarityScore { get; set; }

            public ComparisonResult()
            {
                Differences = new List<CustomizationDifference>();
            }
        }

        public class CustomizationDifference;
        {
            public string OptionId { get; set; }
            public DifferenceType DifferenceType { get; set; }
            public object Value1 { get; set; }
            public object Value2 { get; set; }
            public CustomizationCategory Category { get; set; }
        }

        public class CustomizationPreset;
        {
            public string PresetId { get; set; }
            public string PresetName { get; set; }
            public string CharacterId { get; set; }
            public Dictionary<string, CustomizationSelection> Selections { get; set; }
            public DateTime CreatedAt { get; set; }
            public int UsageCount { get; set; }
            public List<string> Tags { get; set; }

            public CustomizationPreset()
            {
                Selections = new Dictionary<string, CustomizationSelection>();
                Tags = new List<string>();
            }
        }

        public class GeneticResult;
        {
            public bool Success { get; set; }
            public string BaseCharacterId { get; set; }
            public List<CharacterCustomization> Variations { get; set; }
            public int VariationCount { get; set; }
            public string ErrorMessage { get; set; }

            public GeneticResult()
            {
                Variations = new List<CharacterCustomization>();
            }
        }

        public class CustomizationStatistics;
        {
            public string CharacterId { get; set; }
            public int TotalCustomizations { get; set; }
            public Dictionary<CustomizationCategory, int> CustomizationsByCategory { get; set; }
            public DateTime LastUpdated { get; set; }
            public int Version { get; set; }
            public int ActiveSelections { get; set; }
            public float AverageBlendValue { get; set; }

            public CustomizationStatistics()
            {
                CustomizationsByCategory = new Dictionary<CustomizationCategory, int>();
            }
        }

        public class CustomizationHistory;
        {
            public string CharacterId { get; set; }
            public string OptionId { get; set; }
            public List<string> OptionIds { get; set; }
            public CustomizationCategory Category { get; set; }
            public string Action { get; set; }
            public object PreviousValue { get; set; }
            public object NewValue { get; set; }
            public DateTime Timestamp { get; set; }

            public CustomizationHistory()
            {
                OptionIds = new List<string>();
            }
        }

        // Data classes for visualization;
        public class MeshData { }
        public class TextureData { }
        public class MaterialData { }
        public class SkeletonData { }

        #endregion;

        #region Event Definitions;

        public class CharacterCreatedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public string PresetId { get; set; }
            public DateTime Timestamp { get; set; }
            public int CustomizationCount { get; set; }
        }

        public class CharacterSavedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public DateTime Timestamp { get; set; }
            public int Version { get; set; }
        }

        public class CharacterDeletedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CustomizationAppliedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public string OptionId { get; set; }
            public CustomizationCategory Category { get; set; }
            public object Value { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class BatchCustomizationAppliedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public int AppliedCount { get; set; }
            public int FailedCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CustomizationResetEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public string OptionId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CategoryResetEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public CustomizationCategory Category { get; set; }
            public int ResetCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class AllCustomizationsResetEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public int ResetCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CharacterRandomizedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public CustomizationCategory? Category { get; set; }
            public int RandomizedCount { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class PresetAppliedEvent : IEvent;
        {
            public string CharacterId { get; set; }
            public string PresetId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class OptionUpdatedEvent : IEvent;
        {
            public string OptionId { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class CategoryUpdatedEvent : IEvent;
        {
            public CustomizationCategory Category { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public class OptionChangeAffectedCharactersEvent : IEvent;
        {
            public string OptionId { get; set; }
            public List<string> AffectedCharacterIds { get; set; }
            public DateTime Timestamp { get; set; }

            public OptionChangeAffectedCharactersEvent()
            {
                AffectedCharacterIds = new List<string>();
            }
        }

        #endregion;

        #region Interfaces for Dependency Injection;

        public interface ICustomizationRepository;
        {
            Task<CharacterCustomization> LoadCharacterAsync(string characterId);
            Task<bool> SaveCharacterAsync(CharacterCustomization customization);
            Task<bool> DeleteCharacterAsync(string characterId);
            Task<CustomizationPreset> GetPresetAsync(string presetId);
            Task<string> SavePresetAsync(CustomizationPreset preset);
            Task<List<CustomizationPreset>> GetAllPresetsAsync();
        }

        public interface IOptionProvider;
        {
            Task<List<CustomizationOption>> GetAllOptionsAsync();
            Task<CustomizationOption> GetOptionAsync(string optionId);
            Task<List<CustomizationOption>> GetCommonOptionsAsync();
            Task<List<CustomizationOption>> GetDefaultOptionsAsync();
            Task<List<CustomizationOption>> GetOptionsByCategoryAsync(CustomizationCategory category);
        }

        public interface IValidationEngine;
        {
            Task<ValidationResult> ValidateAsync(CharacterCustomization customization, CustomizationOption option, object value);
        }

        public interface IPreviewGenerator;
        {
            Task<CustomizationPreview> GenerateAsync(CharacterCustomization customization);
        }

        #endregion;

        #region Custom Exceptions;

        public class CustomizerException : Exception
        {
            public CustomizerException(string message) : base(message) { }
            public CustomizerException(string message, Exception innerException) : base(message, innerException) { }
        }

        public class CharacterNotFoundException : CustomizerException;
        {
            public CharacterNotFoundException(string message) : base(message) { }
        }

        public class OptionNotFoundException : CustomizerException;
        {
            public OptionNotFoundException(string message) : base(message) { }
        }

        public class PresetNotFoundException : CustomizerException;
        {
            public PresetNotFoundException(string message) : base(message) { }
        }

        #endregion;
    }
}
