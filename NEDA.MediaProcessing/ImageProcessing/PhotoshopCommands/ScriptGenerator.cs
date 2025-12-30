using Microsoft.Identity.Client;
using NEDA.Core.Configuration;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.EngineIntegration.PluginSystem.PluginSDK;
using NEDA.MediaProcessing.ImageProcessing.PhotoshopCommands.ScriptTemplates;
using NEDA.MediaProcessing.ImageProcessing.PhotoshopCommands.Templates;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NEDA.MediaProcessing.ImageProcessing.PhotoshopCommands;
{
    /// <summary>
    /// Photoshop komut betiği oluşturucu - Endüstriyel seviye;
    /// Dinamik betik oluşturma, şablon yönetimi, optimizasyon ve doğrulama;
    /// </summary>
    public interface IScriptGenerator;
    {
        /// <summary>
        /// Photoshop komut betiği oluşturur;
        /// </summary>
        /// <param name="commands">Yürütülecek komutlar</param>
        /// <param name="options">Betik oluşturma seçenekleri</param>
        /// <returns>Oluşturulmuş betik</returns>
        Task<GeneratedScript> GenerateScriptAsync(IEnumerable<PhotoshopCommand> commands, ScriptGenerationOptions options);

        /// <summary>
        /// Şablondan betik oluşturur;
        /// </summary>
        /// <param name="template">Betik şablonu</param>
        /// <param name="parameters">Şablon parametreleri</param>
        /// <returns>Oluşturulmuş betik</returns>
        Task<GeneratedScript> GenerateFromTemplateAsync(ScriptTemplate template, TemplateParameters parameters);

        /// <summary>
        /// JavaScript betiğini oluşturur;
        /// </summary>
        /// <param name="commands">Photoshop komutları</param>
        /// <param name="options">JavaScript seçenekleri</param>
        /// <returns>JavaScript kodu</returns>
        Task<string> GenerateJavaScriptAsync(IEnumerable<PhotoshopCommand> commands, JavaScriptOptions options);

        /// <summary>
        /// ActionScript betiğini oluşturur;
        /// </summary>
        /// <param name="commands">Photoshop komutları</param>
        /// <param name="options">ActionScript seçenekleri</param>
        /// <returns>ActionScript kodu</returns>
        Task<string> GenerateActionScriptAsync(IEnumerable<PhotoshopCommand> commands, ActionScriptOptions options);

        /// <summary>
        /// Visual Basic betiğini oluşturur;
        /// </summary>
        /// <param name="commands">Photoshop komutları</param>
        /// <param name="options">Visual Basic seçenekleri</param>
        /// <returns>Visual Basic kodu</returns>
        Task<string> GenerateVisualBasicScriptAsync(IEnumerable<PhotoshopCommand> commands, VisualBasicOptions options);

        /// <summary>
        /// Betiği optimize eder;
        /// </summary>
        /// <param name="script">Optimize edilecek betik</param>
        /// <returns>Optimize edilmiş betik</returns>
        Task<GeneratedScript> OptimizeScriptAsync(GeneratedScript script);

        /// <summary>
        /// Betik şablonu kaydeder;
        /// </summary>
        /// <param name="template">Kaydedilecek şablon</param>
        Task SaveTemplateAsync(ScriptTemplate template);

        /// <summary>
        /// Betik şablonunu yükler;
        /// </summary>
        /// <param name="templateName">Şablon adı</param>
        /// <returns>Betik şablonu</returns>
        Task<ScriptTemplate> LoadTemplateAsync(string templateName);

        /// <summary>
        /// Betiği doğrular;
        /// </summary>
        /// <param name="script">Doğrulanacak betik</param>
        /// <returns>Doğrulama sonucu</returns>
        Task<ScriptValidationResult> ValidateScriptAsync(GeneratedScript script);

        /// <summary>
        /// Kullanılabilir şablonları listeler;
        /// </summary>
        /// <returns>Şablon listesi</returns>
        Task<IEnumerable<ScriptTemplate>> ListTemplatesAsync();

        /// <summary>
        /// Betik önizlemesi oluşturur;
        /// </summary>
        /// <param name="script">Kaynak betik</param>
        /// <param name="previewType">Önizleme türü</param>
        /// <returns>Önizleme verisi</returns>
        Task<ScriptPreview> GeneratePreviewAsync(GeneratedScript script, PreviewType previewType);
    }

    /// <summary>
    /// Betik oluşturucu - Endüstriyel implementasyon;
    /// </summary>
    public class ScriptGenerator : IScriptGenerator, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IAppConfig _appConfig;
        private readonly ScriptTemplateManager _templateManager;
        private readonly ScriptOptimizer _scriptOptimizer;
        private readonly ScriptValidator _scriptValidator;
        private readonly CodeFormatter _codeFormatter;
        private readonly ConcurrentDictionary<string, IScriptLanguageGenerator> _languageGenerators;
        private readonly ConcurrentDictionary<string, ScriptTemplate> _templateCache;
        private bool _disposed;
        private readonly object _syncLock = new object();

        /// <summary>
        /// ScriptGenerator constructor - Dependency Injection;
        /// </summary>
        public ScriptGenerator(
            ILogger logger,
            IEventBus eventBus,
            IAppConfig appConfig)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _appConfig = appConfig ?? throw new ArgumentNullException(nameof(appConfig));

            _templateManager = new ScriptTemplateManager(logger);
            _scriptOptimizer = new ScriptOptimizer(logger);
            _scriptValidator = new ScriptValidator(logger);
            _codeFormatter = new CodeFormatter(logger);
            _languageGenerators = new ConcurrentDictionary<string, IScriptLanguageGenerator>();
            _templateCache = new ConcurrentDictionary<string, ScriptTemplate>();

            InitializeLanguageGenerators();
            InitializeBuiltInTemplates();

            _logger.Info("ScriptGenerator initialized successfully");
        }

        /// <summary>
        /// Photoshop komut betiği oluşturur;
        /// </summary>
        public async Task<GeneratedScript> GenerateScriptAsync(
            IEnumerable<PhotoshopCommand> commands,
            ScriptGenerationOptions options)
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));
            if (options == null)
                throw new ArgumentNullException(nameof(options));

            var generationId = Guid.NewGuid();
            var startTime = DateTime.UtcNow;

            try
            {
                var commandList = commands.ToList();
                if (!commandList.Any())
                    throw new ArgumentException("Command list cannot be empty", nameof(commands));

                _logger.Info($"Generating script: {generationId}");
                _logger.Info($"Commands: {commandList.Count}, Output type: {options.OutputType}, Optimize: {options.Optimize}");

                // Komutları doğrula;
                await ValidateCommandsAsync(commandList);

                // Komutları optimize et;
                var optimizedCommands = options.Optimize;
                    ? await OptimizeCommandsAsync(commandList)
                    : commandList;

                if (optimizedCommands.Count != commandList.Count)
                {
                    _logger.Debug($"Commands optimized: {commandList.Count} -> {optimizedCommands.Count}");
                }

                // Betiği oluştur;
                string scriptContent;
                ScriptLanguage language;

                switch (options.OutputType)
                {
                    case ScriptOutputType.JavaScript:
                        var jsOptions = options.JavaScriptOptions ?? new JavaScriptOptions();
                        scriptContent = await GenerateJavaScriptAsync(optimizedCommands, jsOptions);
                        language = ScriptLanguage.JavaScript;
                        break;

                    case ScriptOutputType.ActionScript:
                        var asOptions = options.ActionScriptOptions ?? new ActionScriptOptions();
                        scriptContent = await GenerateActionScriptAsync(optimizedCommands, asOptions);
                        language = ScriptLanguage.ActionScript;
                        break;

                    case ScriptOutputType.VisualBasic:
                        var vbOptions = options.VisualBasicOptions ?? new VisualBasicOptions();
                        scriptContent = await GenerateVisualBasicScriptAsync(optimizedCommands, vbOptions);
                        language = ScriptLanguage.VisualBasic;
                        break;

                    default:
                        throw new NotSupportedException($"Output type not supported: {options.OutputType}");
                }

                // Betik formatını uygula;
                if (options.FormatCode)
                {
                    scriptContent = await _codeFormatter.FormatAsync(scriptContent, language);
                }

                // Betiği oluştur;
                var generatedScript = new GeneratedScript;
                {
                    Id = generationId,
                    Name = options.ScriptName ?? $"GeneratedScript_{generationId:N}",
                    Description = options.Description,
                    Content = scriptContent,
                    Language = language,
                    OutputType = options.OutputType,
                    GeneratedTime = DateTime.UtcNow,
                    GenerationTime = DateTime.UtcNow - startTime,
                    Metadata = new Dictionary<string, object>
                    {
                        ["OriginalCommandCount"] = commandList.Count,
                        ["OptimizedCommandCount"] = optimizedCommands.Count,
                        ["Options"] = options,
                        ["FormatApplied"] = options.FormatCode;
                    }
                };

                // Optimizasyon raporu ekle;
                if (options.Optimize && commandList.Count != optimizedCommands.Count)
                {
                    generatedScript.OptimizationReport = GenerateOptimizationReport(commandList, optimizedCommands);
                }

                // Betiği doğrula;
                if (options.Validate)
                {
                    var validationResult = await ValidateScriptAsync(generatedScript);
                    generatedScript.ValidationResult = validationResult;

                    if (!validationResult.IsValid && options.ThrowOnValidationError)
                    {
                        throw new ScriptValidationException($"Script validation failed: {validationResult.Errors.FirstOrDefault()?.Message}");
                    }
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new ScriptGeneratedEvent;
                {
                    GenerationId = generationId,
                    ScriptName = generatedScript.Name,
                    Language = language,
                    CommandCount = optimizedCommands.Count,
                    ScriptSize = scriptContent.Length,
                    GenerationTime = generatedScript.GenerationTime,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Script generated successfully: {generatedScript.Name}");
                _logger.Debug($"Script size: {scriptContent.Length} chars, " +
                            $"Generation time: {generatedScript.GenerationTime.TotalMilliseconds:F2}ms, " +
                            $"Language: {language}");

                return generatedScript;
            }
            catch (ScriptGenerationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script generation failed: {ex.Message}", ex);
                await HandleGenerationFailureAsync(generationId, startTime, ex);
                throw new ScriptGenerationException($"Script generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Şablondan betik oluşturur;
        /// </summary>
        public async Task<GeneratedScript> GenerateFromTemplateAsync(
            ScriptTemplate template,
            TemplateParameters parameters)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            var generationId = Guid.NewGuid();

            try
            {
                _logger.Info($"Generating script from template: {template.Name} (ID: {generationId})");
                _logger.Debug($"Template version: {template.Version}, Parameters: {parameters.Values.Count}");

                // Şablonu doğrula;
                await ValidateTemplateAsync(template);

                // Parametreleri doğrula;
                await ValidateTemplateParametersAsync(template, parameters);

                // Şablonu yükle;
                var templateContent = await LoadTemplateContentAsync(template);

                // Parametreleri uygula;
                var processedContent = await ApplyTemplateParametersAsync(templateContent, parameters);

                // Dinamik komutları oluştur;
                var dynamicCommands = await GenerateDynamicCommandsAsync(template, parameters);

                // Ek komutları uygula;
                if (parameters.AdditionalCommands?.Any() == true)
                {
                    dynamicCommands.AddRange(parameters.AdditionalCommands);
                }

                // Betik oluşturma seçenekleri;
                var options = new ScriptGenerationOptions;
                {
                    ScriptName = parameters.ScriptName ?? $"{template.Name}_{DateTime.Now:yyyyMMdd_HHmmss}",
                    Description = parameters.Description ?? template.Description,
                    OutputType = template.OutputType,
                    Optimize = parameters.Optimize,
                    Validate = parameters.Validate,
                    FormatCode = parameters.FormatCode,
                    JavaScriptOptions = template.JavaScriptOptions,
                    ActionScriptOptions = template.ActionScriptOptions,
                    VisualBasicOptions = template.VisualBasicOptions;
                };

                // Dinamik komutlarla betik oluştur;
                var generatedScript = await GenerateScriptAsync(dynamicCommands, options);

                // Şablon özelliklerini ekle;
                generatedScript.TemplateName = template.Name;
                generatedScript.TemplateVersion = template.Version;
                generatedScript.TemplateParameters = parameters.Values;

                // Şablon içeriğini ekle (eğer şablon kod bloğu içeriyorsa)
                if (!string.IsNullOrEmpty(processedContent))
                {
                    generatedScript.TemplateContent = processedContent;
                }

                // Olay yayınla;
                await _eventBus.PublishAsync(new TemplateScriptGeneratedEvent;
                {
                    GenerationId = generationId,
                    TemplateName = template.Name,
                    TemplateVersion = template.Version,
                    ScriptName = generatedScript.Name,
                    ParameterCount = parameters.Values.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Template script generated successfully: {template.Name} -> {generatedScript.Name}");

                return generatedScript;
            }
            catch (Exception ex)
            {
                _logger.Error($"Template script generation failed: {ex.Message}", ex);
                throw new ScriptGenerationException($"Template script generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// JavaScript betiğini oluşturur;
        /// </summary>
        public async Task<string> GenerateJavaScriptAsync(
            IEnumerable<PhotoshopCommand> commands,
            JavaScriptOptions options)
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));

            try
            {
                var commandList = commands.ToList();
                if (!commandList.Any())
                    throw new ArgumentException("Command list cannot be empty", nameof(commands));

                _logger.Debug($"Generating JavaScript script for {commandList.Count} commands");

                // JavaScript üreticisini al;
                var generator = GetLanguageGenerator(ScriptLanguage.JavaScript);

                // Komutları işle;
                var processedCommands = await ProcessCommandsForJavaScriptAsync(commandList, options);

                // Betiği oluştur;
                var script = await generator.GenerateAsync(processedCommands, options);

                // Hata yönetimi ekle;
                script = AddErrorHandling(script, options.ErrorHandling);

                // Yorumları ekle;
                if (options.AddComments)
                {
                    script = AddComments(script, commandList, options);
                }

                // Başlık ekle;
                if (options.AddHeader)
                {
                    script = AddJavaScriptHeader(script, options, commandList.Count);
                }

                // Son kontrol;
                script = await ValidateJavaScriptSyntaxAsync(script);

                _logger.Debug($"JavaScript script generated: {script.Length} chars");

                return script;
            }
            catch (Exception ex)
            {
                _logger.Error($"JavaScript generation failed: {ex.Message}", ex);
                throw new ScriptGenerationException($"JavaScript generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// ActionScript betiğini oluşturur;
        /// </summary>
        public async Task<string> GenerateActionScriptAsync(
            IEnumerable<PhotoshopCommand> commands,
            ActionScriptOptions options)
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));

            try
            {
                var commandList = commands.ToList();
                if (!commandList.Any())
                    throw new ArgumentException("Command list cannot be empty", nameof(commands));

                _logger.Debug($"Generating ActionScript script for {commandList.Count} commands");

                // ActionScript üreticisini al;
                var generator = GetLanguageGenerator(ScriptLanguage.ActionScript);

                // Komutları işle;
                var processedCommands = await ProcessCommandsForActionScriptAsync(commandList, options);

                // Betiği oluştur;
                var script = await generator.GenerateAsync(processedCommands, options);

                // Hata yönetimi ekle;
                script = AddActionScriptErrorHandling(script, options.ErrorHandling);

                // Yorumları ekle;
                if (options.AddComments)
                {
                    script = AddActionScriptComments(script, commandList, options);
                }

                // Başlık ekle;
                if (options.AddHeader)
                {
                    script = AddActionScriptHeader(script, options, commandList.Count);
                }

                _logger.Debug($"ActionScript script generated: {script.Length} chars");

                return script;
            }
            catch (Exception ex)
            {
                _logger.Error($"ActionScript generation failed: {ex.Message}", ex);
                throw new ScriptGenerationException($"ActionScript generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Visual Basic betiğini oluşturur;
        /// </summary>
        public async Task<string> GenerateVisualBasicScriptAsync(
            IEnumerable<PhotoshopCommand> commands,
            VisualBasicOptions options)
        {
            if (commands == null)
                throw new ArgumentNullException(nameof(commands));

            try
            {
                var commandList = commands.ToList();
                if (!commandList.Any())
                    throw new ArgumentException("Command list cannot be empty", nameof(commands));

                _logger.Debug($"Generating Visual Basic script for {commandList.Count} commands");

                // Visual Basic üreticisini al;
                var generator = GetLanguageGenerator(ScriptLanguage.VisualBasic);

                // Komutları işle;
                var processedCommands = await ProcessCommandsForVisualBasicAsync(commandList, options);

                // Betiği oluştur;
                var script = await generator.GenerateAsync(processedCommands, options);

                // Hata yönetimi ekle;
                script = AddVisualBasicErrorHandling(script, options.ErrorHandling);

                // Yorumları ekle;
                if (options.AddComments)
                {
                    script = AddVisualBasicComments(script, commandList, options);
                }

                // Başlık ekle;
                if (options.AddHeader)
                {
                    script = AddVisualBasicHeader(script, options, commandList.Count);
                }

                _logger.Debug($"Visual Basic script generated: {script.Length} chars");

                return script;
            }
            catch (Exception ex)
            {
                _logger.Error($"Visual Basic generation failed: {ex.Message}", ex);
                throw new ScriptGenerationException($"Visual Basic generation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Betiği optimize eder;
        /// </summary>
        public async Task<GeneratedScript> OptimizeScriptAsync(GeneratedScript script)
        {
            if (script == null)
                throw new ArgumentNullException(nameof(script));

            try
            {
                _logger.Info($"Optimizing script: {script.Name} (ID: {script.Id})");
                _logger.Debug($"Original size: {script.Content.Length} chars, Language: {script.Language}");

                // Optimizasyon öncesi durumu kaydet;
                var originalSize = script.Content.Length;
                var originalHash = ComputeContentHash(script.Content);

                // Betiği optimize et;
                var optimizedContent = await _scriptOptimizer.OptimizeAsync(script.Content, script.Language);

                // Farkı hesapla;
                var optimizationResult = new OptimizationResult;
                {
                    OriginalSize = originalSize,
                    OptimizedSize = optimizedContent.Length,
                    SizeReduction = originalSize - optimizedContent.Length,
                    ReductionPercentage = originalSize > 0 ? (double)(originalSize - optimizedContent.Length) / originalSize * 100 : 0,
                    OriginalHash = originalHash,
                    OptimizedHash = ComputeContentHash(optimizedContent),
                    OptimizationTime = DateTime.UtcNow,
                    AppliedOptimizations = _scriptOptimizer.GetAppliedOptimizations()
                };

                // Optimize edilmiş betiği oluştur;
                var optimizedScript = script.Clone();
                optimizedScript.Content = optimizedContent;
                optimizedScript.Optimized = true;
                optimizedScript.OptimizationResult = optimizationResult;
                optimizedScript.Metadata["OriginalScriptId"] = script.Id;
                optimizedScript.Metadata["OptimizationTime"] = DateTime.UtcNow;

                // Olay yayınla;
                await _eventBus.PublishAsync(new ScriptOptimizedEvent;
                {
                    ScriptId = script.Id,
                    ScriptName = script.Name,
                    OriginalSize = originalSize,
                    OptimizedSize = optimizedContent.Length,
                    ReductionPercentage = optimizationResult.ReductionPercentage,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Script optimized successfully: {script.Name}");
                _logger.Info($"Size reduction: {optimizationResult.SizeReduction} chars " +
                           $"({optimizationResult.ReductionPercentage:F2}%)");

                return optimizedScript;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script optimization failed: {ex.Message}", ex);
                throw new ScriptOptimizationException($"Script optimization failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Betik şablonu kaydeder;
        /// </summary>
        public async Task SaveTemplateAsync(ScriptTemplate template)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));

            try
            {
                // Şablonu doğrula;
                await ValidateTemplateAsync(template);

                // Şablonu kaydet;
                await _templateManager.SaveTemplateAsync(template);

                // Önbelleğe ekle;
                _templateCache[template.Name] = template;

                _logger.Info($"Script template saved: {template.Name} (Version: {template.Version})");

                // Olay yayınla;
                await _eventBus.PublishAsync(new TemplateSavedEvent;
                {
                    TemplateName = template.Name,
                    TemplateVersion = template.Version,
                    Category = template.Category,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save template: {ex.Message}", ex);
                throw new TemplateManagementException($"Failed to save template: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Betik şablonunu yükler;
        /// </summary>
        public async Task<ScriptTemplate> LoadTemplateAsync(string templateName)
        {
            if (string.IsNullOrWhiteSpace(templateName))
                throw new ArgumentException("Template name cannot be empty", nameof(templateName));

            try
            {
                // Önbellekten kontrol et;
                if (_templateCache.TryGetValue(templateName, out var cachedTemplate))
                {
                    _logger.Debug($"Template loaded from cache: {templateName}");
                    return cachedTemplate;
                }

                // Şablonu yükle;
                var template = await _templateManager.LoadTemplateAsync(templateName);

                if (template == null)
                {
                    _logger.Warn($"Template not found: {templateName}");
                    throw new TemplateNotFoundException($"Template not found: {templateName}");
                }

                // Önbelleğe ekle;
                _templateCache[templateName] = template;

                _logger.Info($"Script template loaded: {templateName} (Version: {template.Version})");

                return template;
            }
            catch (TemplateNotFoundException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load template: {ex.Message}", ex);
                throw new TemplateManagementException($"Failed to load template: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Betiği doğrular;
        /// </summary>
        public async Task<ScriptValidationResult> ValidateScriptAsync(GeneratedScript script)
        {
            if (script == null)
                throw new ArgumentNullException(nameof(script));

            try
            {
                _logger.Debug($"Validating script: {script.Name}");

                var validationResult = new ScriptValidationResult;
                {
                    ScriptId = script.Id,
                    ScriptName = script.Name,
                    Language = script.Language,
                    ValidationTime = DateTime.UtcNow;
                };

                // Temel doğrulamalar;
                ValidateBasicScriptProperties(script, validationResult);

                if (!validationResult.IsValid)
                {
                    return validationResult;
                }

                // Sözdizimi doğrulama;
                await ValidateScriptSyntaxAsync(script, validationResult);

                // Semantik doğrulama;
                await ValidateScriptSemanticsAsync(script, validationResult);

                // Performans doğrulama;
                await ValidateScriptPerformanceAsync(script, validationResult);

                // Güvenlik doğrulama;
                await ValidateScriptSecurityAsync(script, validationResult);

                _logger.Debug($"Script validation completed: {script.Name}, Valid: {validationResult.IsValid}");

                return validationResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Script validation failed: {ex.Message}", ex);
                throw new ScriptValidationException($"Script validation failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Kullanılabilir şablonları listeler;
        /// </summary>
        public async Task<IEnumerable<ScriptTemplate>> ListTemplatesAsync()
        {
            try
            {
                var templates = await _templateManager.ListTemplatesAsync();

                // Önbelleği güncelle;
                foreach (var template in templates)
                {
                    _templateCache[template.Name] = template;
                }

                _logger.Debug($"Listed {templates.Count()} script templates");

                return templates;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to list templates: {ex.Message}", ex);
                throw new TemplateManagementException($"Failed to list templates: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Betik önizlemesi oluşturur;
        /// </summary>
        public async Task<ScriptPreview> GeneratePreviewAsync(
            GeneratedScript script,
            PreviewType previewType)
        {
            if (script == null)
                throw new ArgumentNullException(nameof(script));

            try
            {
                _logger.Debug($"Generating script preview: {script.Name}, Type: {previewType}");

                var preview = new ScriptPreview;
                {
                    ScriptId = script.Id,
                    ScriptName = script.Name,
                    PreviewType = previewType,
                    GeneratedTime = DateTime.UtcNow;
                };

                switch (previewType)
                {
                    case PreviewType.CodeSnippet:
                        preview.Content = GenerateCodeSnippet(script.Content, 50); // İlk 50 satır;
                        preview.Metadata["LineCount"] = 50;
                        break;

                    case PreviewType.Structure:
                        preview.Content = GenerateStructurePreview(script);
                        break;

                    case PreviewType.Commands:
                        preview.Content = await GenerateCommandsPreviewAsync(script);
                        break;

                    case PreviewType.Statistics:
                        preview.Content = GenerateStatisticsPreview(script);
                        break;

                    case PreviewType.Full:
                        preview.Content = script.Content;
                        preview.Metadata["FullScript"] = true;
                        break;

                    default:
                        throw new NotSupportedException($"Preview type not supported: {previewType}");
                }

                // Önizleme boyutunu sınırla;
                if (preview.Content.Length > 10000 && previewType != PreviewType.Full)
                {
                    preview.Content = preview.Content.Substring(0, 10000) + "\n\n... (truncated)";
                    preview.Metadata["Truncated"] = true;
                }

                _logger.Debug($"Script preview generated: {script.Name}, Size: {preview.Content.Length} chars");

                return preview;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate script preview: {ex.Message}", ex);
                throw new ScriptPreviewException($"Failed to generate script preview: {ex.Message}", ex);
            }
        }

        #region Private Methods;

        private void InitializeLanguageGenerators()
        {
            try
            {
                // Dil üreticilerini kaydet;
                _languageGenerators[ScriptLanguage.JavaScript.ToString()] = new JavaScriptGenerator(_logger);
                _languageGenerators[ScriptLanguage.ActionScript.ToString()] = new ActionScriptGenerator(_logger);
                _languageGenerators[ScriptLanguage.VisualBasic.ToString()] = new VisualBasicGenerator(_logger);

                _logger.Info($"Initialized {_languageGenerators.Count} language generators");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize language generators: {ex.Message}", ex);
                throw new ScriptGenerationException($"Failed to initialize language generators: {ex.Message}", ex);
            }
        }

        private void InitializeBuiltInTemplates()
        {
            try
            {
                // Yerleşik şablonları yükle;
                var builtInTemplates = new List<ScriptTemplate>
                {
                    CreateBatchProcessingTemplate(),
                    CreateImageResizeTemplate(),
                    CreateWatermarkTemplate(),
                    CreateColorCorrectionTemplate(),
                    CreateFilterApplicationTemplate(),
                    CreateExportTemplate(),
                    CreateLayerManagementTemplate(),
                    CreateTextEffectTemplate()
                };

                foreach (var template in builtInTemplates)
                {
                    _templateManager.RegisterBuiltInTemplate(template);
                    _templateCache[template.Name] = template;
                }

                _logger.Info($"Initialized {builtInTemplates.Count} built-in templates");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize built-in templates: {ex.Message}", ex);
                // Şablon yükleme hatası kritik değil, devam et;
            }
        }

        private IScriptLanguageGenerator GetLanguageGenerator(ScriptLanguage language)
        {
            var key = language.ToString();

            if (_languageGenerators.TryGetValue(key, out var generator))
            {
                return generator;
            }

            throw new LanguageGeneratorNotFoundException($"Language generator not found for: {language}");
        }

        private async Task ValidateCommandsAsync(List<PhotoshopCommand> commands)
        {
            var validationErrors = new List<string>();

            foreach (var command in commands)
            {
                // Komut adı kontrolü;
                if (string.IsNullOrWhiteSpace(command.Name))
                {
                    validationErrors.Add($"Command name cannot be empty at position {commands.IndexOf(command)}");
                }

                // Komut türü kontrolü;
                if (!Enum.IsDefined(typeof(CommandType), command.Type))
                {
                    validationErrors.Add($"Invalid command type: {command.Type} for command {command.Name}");
                }

                // Gereksiz parametre kontrolü;
                if (command.Parameters != null)
                {
                    foreach (var param in command.Parameters)
                    {
                        if (param.Value == null || (param.Value is string str && string.IsNullOrWhiteSpace(str)))
                        {
                            validationErrors.Add($"Parameter '{param.Key}' has empty value in command {command.Name}");
                        }
                    }
                }
            }

            if (validationErrors.Any())
            {
                throw new CommandValidationException($"Command validation failed:\n{string.Join("\n", validationErrors)}");
            }

            await Task.CompletedTask;
        }

        private async Task<List<PhotoshopCommand>> OptimizeCommandsAsync(List<PhotoshopCommand> commands)
        {
            var optimized = new List<PhotoshopCommand>();
            var i = 0;

            while (i < commands.Count)
            {
                var currentCommand = commands[i];
                var nextCommand = i + 1 < commands.Count ? commands[i + 1] : null;

                // Komut birleştirme kontrolü;
                if (nextCommand != null && CanMergeCommands(currentCommand, nextCommand))
                {
                    var mergedCommand = MergeCommands(currentCommand, nextCommand);
                    optimized.Add(mergedCommand);
                    i += 2; // İki komutu atla;
                }
                else;
                {
                    // Gereksiz komut kontrolü;
                    if (!IsRedundantCommand(currentCommand, optimized))
                    {
                        optimized.Add(currentCommand);
                    }
                    i++;
                }
            }

            await Task.CompletedTask;
            return optimized;
        }

        private bool CanMergeCommands(PhotoshopCommand command1, PhotoshophopCommand command2)
        {
            // Aynı türde komutları birleştirme kontrolü;
            if (command1.Type != command2.Type)
                return false;

            // Belirli komut türleri için birleştirme mantığı;
            switch (command1.Type)
            {
                case CommandType.Resize:
                    // Art arda resize komutlarını birleştir;
                    return true;

                case CommandType.AdjustColor:
                    // Renk ayarı komutlarını birleştir;
                    return true;

                case CommandType.ApplyFilter:
                    // Aynı filtre türünde ise birleştir;
                    return command1.Parameters?.GetValueOrDefault("FilterName") ==
                           command2.Parameters?.GetValueOrDefault("FilterName");

                default:
                    return false;
            }
        }

        private PhotoshopCommand MergeCommands(PhotoshopCommand command1, PhotoshopCommand command2)
        {
            // Komutları birleştir;
            var merged = new PhotoshopCommand;
            {
                Name = $"{command1.Name}_Merged",
                Type = command1.Type,
                Description = $"Merged: {command1.Description} + {command2.Description}"
            };

            // Parametreleri birleştir;
            if (command1.Parameters != null || command2.Parameters != null)
            {
                merged.Parameters = new Dictionary<string, object>();

                if (command1.Parameters != null)
                {
                    foreach (var param in command1.Parameters)
                    {
                        merged.Parameters[param.Key] = param.Value;
                    }
                }

                if (command2.Parameters != null)
                {
                    foreach (var param in command2.Parameters)
                    {
                        // Çakışan parametreler için ikinci komutun değerini kullan;
                        merged.Parameters[param.Key] = param.Value;
                    }
                }
            }

            return merged;
        }

        private bool IsRedundantCommand(PhotoshopCommand command, List<PhotoshopCommand> currentPipeline)
        {
            if (currentPipeline.Count == 0)
                return false;

            var lastCommand = currentPipeline.Last();

            // Aynı komutun art arda tekrarı;
            if (lastCommand.Type == command.Type &&
                AreCommandsIdentical(lastCommand, command))
            {
                return true;
            }

            // Birbirini iptal eden komutlar;
            if (AreCommandsCancelling(lastCommand, command))
            {
                return true;
            }

            return false;
        }

        private bool AreCommandsIdentical(PhotoshopCommand cmd1, PhotoshopCommand cmd2)
        {
            if (cmd1.Type != cmd2.Type)
                return false;

            if (cmd1.Parameters == null && cmd2.Parameters == null)
                return true;

            if (cmd1.Parameters == null || cmd2.Parameters == null)
                return false;

            if (cmd1.Parameters.Count != cmd2.Parameters.Count)
                return false;

            foreach (var param in cmd1.Parameters)
            {
                if (!cmd2.Parameters.TryGetValue(param.Key, out var value2) ||
                    !param.Value.Equals(value2))
                {
                    return false;
                }
            }

            return true;
        }

        private bool AreCommandsCancelling(PhotoshopCommand cmd1, PhotoshopCommand cmd2)
        {
            // Örnek: Brightness +10 ve sonra -10 birbirini iptal eder;
            if (cmd1.Type == CommandType.AdjustColor && cmd2.Type == CommandType.AdjustColor)
            {
                var param1 = cmd1.Parameters?.GetValueOrDefault("Brightness");
                var param2 = cmd2.Parameters?.GetValueOrDefault("Brightness");

                if (param1 is int brightness1 && param2 is int brightness2)
                {
                    return brightness1 + brightness2 == 0;
                }
            }

            return false;
        }

        private OptimizationReport GenerateOptimizationReport(
            List<PhotoshopCommand> originalCommands,
            List<PhotoshopCommand> optimizedCommands)
        {
            var report = new OptimizationReport;
            {
                OriginalCommandCount = originalCommands.Count,
                OptimizedCommandCount = optimizedCommands.Count,
                RemovedCommands = originalCommands.Count - optimizedCommands.Count,
                OptimizationRate = (double)(originalCommands.Count - optimizedCommands.Count) / originalCommands.Count * 100,
                OptimizationTime = DateTime.UtcNow;
            };

            // Kaldırılan komutları belirle;
            var removed = new List<string>();
            var optimizedSet = new HashSet<string>(optimizedCommands.Select(c => c.Name));

            foreach (var cmd in originalCommands)
            {
                if (!optimizedSet.Contains(cmd.Name))
                {
                    removed.Add(cmd.Name);
                }
            }

            report.RemovedCommandNames = removed;

            // Birleştirilen komutları belirle;
            var mergedCommands = new List<string>();
            // Burada daha detaylı analiz yapılabilir;

            report.MergedCommands = mergedCommands;

            return report;
        }

        private async Task ValidateTemplateAsync(ScriptTemplate template)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(template.Name))
            {
                errors.Add("Template name cannot be empty");
            }

            if (string.IsNullOrWhiteSpace(template.Content) && template.Commands?.Any() != true)
            {
                errors.Add("Template must have either content or commands");
            }

            if (!Enum.IsDefined(typeof(ScriptOutputType), template.OutputType))
            {
                errors.Add($"Invalid output type: {template.OutputType}");
            }

            if (errors.Any())
            {
                throw new TemplateValidationException($"Template validation failed:\n{string.Join("\n", errors)}");
            }

            await Task.CompletedTask;
        }

        private async Task ValidateTemplateParametersAsync(ScriptTemplate template, TemplateParameters parameters)
        {
            var errors = new List<string>();

            // Gerekli parametreleri kontrol et;
            foreach (var paramDef in template.RequiredParameters)
            {
                if (!parameters.Values.ContainsKey(paramDef.Key))
                {
                    errors.Add($"Required parameter missing: {paramDef.Key}");
                }
                else;
                {
                    // Parametre türünü doğrula;
                    var value = parameters.Values[paramDef.Key];
                    if (!IsValidParameterType(value, paramDef.Value.Type))
                    {
                        errors.Add($"Invalid type for parameter {paramDef.Key}. Expected: {paramDef.Value.Type}");
                    }

                    // Parametre değer aralığını kontrol et;
                    if (paramDef.Value.MinValue != null && paramDef.Value.MaxValue != null)
                    {
                        if (!IsWithinRange(value, paramDef.Value.MinValue, paramDef.Value.MaxValue))
                        {
                            errors.Add($"Parameter {paramDef.Key} value out of range");
                        }
                    }
                }
            }

            if (errors.Any())
            {
                throw new ParameterValidationException($"Parameter validation failed:\n{string.Join("\n", errors)}");
            }

            await Task.CompletedTask;
        }

        private bool IsValidParameterType(object value, ParameterType type)
        {
            return type switch;
            {
                ParameterType.Integer => value is int,
                ParameterType.Double => value is double,
                ParameterType.String => value is string,
                ParameterType.Boolean => value is bool,
                ParameterType.FilePath => value is string,
                ParameterType.DirectoryPath => value is string,
                _ => true;
            };
        }

        private bool IsWithinRange(object value, object min, object max)
        {
            if (value is IComparable comparable && min is IComparable minComparable && max is IComparable maxComparable)
            {
                return comparable.CompareTo(minComparable) >= 0 && comparable.CompareTo(maxComparable) <= 0;
            }
            return false;
        }

        private async Task<string> LoadTemplateContentAsync(ScriptTemplate template)
        {
            if (!string.IsNullOrEmpty(template.Content))
            {
                return template.Content;
            }

            // Komutlardan içerik oluştur;
            if (template.Commands?.Any() == true)
            {
                var options = new ScriptGenerationOptions;
                {
                    OutputType = template.OutputType,
                    Optimize = false,
                    Validate = false;
                };

                var generatedScript = await GenerateScriptAsync(template.Commands, options);
                return generatedScript.Content;
            }

            return string.Empty;
        }

        private async Task<string> ApplyTemplateParametersAsync(string templateContent, TemplateParameters parameters)
        {
            if (string.IsNullOrEmpty(templateContent))
                return templateContent;

            var processedContent = templateContent;

            // Parametre değiştiricilerini uygula;
            foreach (var param in parameters.Values)
            {
                var placeholder = $"{{{{{param.Key}}}}}";
                processedContent = processedContent.Replace(placeholder, param.Value.ToString());
            }

            // Koşullu blokları işle;
            processedContent = await ProcessConditionalBlocksAsync(processedContent, parameters);

            // Döngü bloklarını işle;
            processedContent = await ProcessLoopBlocksAsync(processedContent, parameters);

            await Task.CompletedTask;
            return processedContent;
        }

        private async Task<string> ProcessConditionalBlocksAsync(string content, TemplateParameters parameters)
        {
            // {{if condition}}...{{endif}} bloklarını işle;
            var result = content;
            var startIndex = 0;

            while ((startIndex = result.IndexOf("{{if", startIndex)) != -1)
            {
                var endIfIndex = result.IndexOf("{{endif}}", startIndex);
                if (endIfIndex == -1)
                    break;

                var conditionStart = result.IndexOf(" ", startIndex + 4) + 1;
                var conditionEnd = result.IndexOf("}}", startIndex);

                if (conditionStart > 0 && conditionEnd > conditionStart)
                {
                    var condition = result.Substring(conditionStart, conditionEnd - conditionStart).Trim();
                    var blockContent = result.Substring(conditionEnd + 2, endIfIndex - (conditionEnd + 2));

                    // Koşulu değerlendir;
                    var conditionResult = EvaluateCondition(condition, parameters);

                    if (conditionResult)
                    {
                        result = result.Remove(startIndex, endIfIndex + 9 - startIndex);
                        result = result.Insert(startIndex, blockContent);
                    }
                    else;
                    {
                        result = result.Remove(startIndex, endIfIndex + 9 - startIndex);
                    }
                }
                else;
                {
                    startIndex += 4;
                }
            }

            await Task.CompletedTask;
            return result;
        }

        private bool EvaluateCondition(string condition, TemplateParameters parameters)
        {
            try
            {
                // Basit koşul değerlendirmesi;
                // Örnek: "width > 100"
                var parts = condition.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length >= 3)
                {
                    var paramName = parts[0];
                    var op = parts[1];
                    var value = parts[2];

                    if (parameters.Values.TryGetValue(paramName, out var paramValue))
                    {
                        if (paramValue is IComparable comparable && double.TryParse(value, out var numericValue))
                        {
                            var comparison = comparable.CompareTo(numericValue);

                            return op switch;
                            {
                                ">" => comparison > 0,
                                "<" => comparison < 0,
                                ">=" => comparison >= 0,
                                "<=" => comparison <= 0,
                                "==" => comparison == 0,
                                "!=" => comparison != 0,
                                _ => false;
                            };
                        }
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private async Task<string> ProcessLoopBlocksAsync(string content, TemplateParameters parameters)
        {
            // {{loop items}}...{{endloop}} bloklarını işle;
            var result = content;
            var startIndex = 0;

            while ((startIndex = result.IndexOf("{{loop", startIndex)) != -1)
            {
                var endLoopIndex = result.IndexOf("{{endloop}}", startIndex);
                if (endLoopIndex == -1)
                    break;

                var itemsStart = result.IndexOf(" ", startIndex + 6) + 1;
                var itemsEnd = result.IndexOf("}}", startIndex);

                if (itemsStart > 0 && itemsEnd > itemsStart)
                {
                    var itemsName = result.Substring(itemsStart, itemsEnd - itemsStart).Trim();
                    var blockContent = result.Substring(itemsEnd + 2, endLoopIndex - (itemsEnd + 2));

                    if (parameters.Values.TryGetValue(itemsName, out var itemsValue) &&
                        itemsValue is IEnumerable<object> items)
                    {
                        var loopResult = new StringBuilder();
                        var itemIndex = 0;

                        foreach (var item in items)
                        {
                            var itemContent = blockContent.Replace("{{item}}", item.ToString());
                            itemContent = itemContent.Replace("{{index}}", itemIndex.ToString());
                            loopResult.AppendLine(itemContent);
                            itemIndex++;
                        }

                        result = result.Remove(startIndex, endLoopIndex + 11 - startIndex);
                        result = result.Insert(startIndex, loopResult.ToString());
                    }
                    else;
                    {
                        result = result.Remove(startIndex, endLoopIndex + 11 - startIndex);
                    }
                }
                else;
                {
                    startIndex += 6;
                }
            }

            await Task.CompletedTask;
            return result;
        }

        private async Task<List<PhotoshopCommand>> GenerateDynamicCommandsAsync(ScriptTemplate template, TemplateParameters parameters)
        {
            var commands = new List<PhotoshopCommand>();

            if (template.Commands?.Any() == true)
            {
                // Şablon komutlarını klonla;
                foreach (var templateCommand in template.Commands)
                {
                    var command = templateCommand.Clone();

                    // Komut parametrelerinde değişkenleri değiştir;
                    if (command.Parameters != null)
                    {
                        foreach (var paramKey in command.Parameters.Keys.ToList())
                        {
                            var paramValue = command.Parameters[paramKey];
                            if (paramValue is string stringValue)
                            {
                                // {{variable}} yerine gerçek değeri koy;
                                foreach (var param in parameters.Values)
                                {
                                    var placeholder = $"{{{{{param.Key}}}}}";
                                    if (stringValue.Contains(placeholder))
                                    {
                                        stringValue = stringValue.Replace(placeholder, param.Value.ToString());
                                    }
                                }
                                command.Parameters[paramKey] = stringValue;
                            }
                        }
                    }

                    commands.Add(command);
                }
            }

            await Task.CompletedTask;
            return commands;
        }

        private async Task<List<PhotoshopCommand>> ProcessCommandsForJavaScriptAsync(
            List<PhotoshopCommand> commands,
            JavaScriptOptions options)
        {
            var processed = new List<PhotoshopCommand>();

            foreach (var command in commands)
            {
                var processedCommand = command.Clone();

                // JavaScript'e özel işlemler;
                switch (command.Type)
                {
                    case CommandType.OpenFile:
                        // Dosya yolunu normalize et;
                        if (processedCommand.Parameters != null &&
                            processedCommand.Parameters.ContainsKey("FilePath"))
                        {
                            var filePath = processedCommand.Parameters["FilePath"] as string;
                            if (!string.IsNullOrEmpty(filePath))
                            {
                                // Windows yolunu JavaScript için uygun hale getir;
                                filePath = filePath.Replace("\\", "/");
                                processedCommand.Parameters["FilePath"] = filePath;
                            }
                        }
                        break;

                    case CommandType.SaveFile:
                        // Kaydetme seçeneklerini ekle;
                        if (processedCommand.Parameters == null)
                        {
                            processedCommand.Parameters = new Dictionary<string, object>();
                        }

                        if (!processedCommand.Parameters.ContainsKey("Quality"))
                        {
                            processedCommand.Parameters["Quality"] = options.DefaultSaveQuality;
                        }

                        if (!processedCommand.Parameters.ContainsKey("Format"))
                        {
                            processedCommand.Parameters["Format"] = options.DefaultSaveFormat;
                        }
                        break;
                }

                processed.Add(processedCommand);
            }

            await Task.CompletedTask;
            return processed;
        }

        private async Task<List<PhotoshopCommand>> ProcessCommandsForActionScriptAsync(
            List<PhotoshopCommand> commands,
            ActionScriptOptions options)
        {
            var processed = new List<PhotoshopCommand>();

            foreach (var command in commands)
            {
                var processedCommand = command.Clone();

                // ActionScript'e özel işlemler;
                if (processedCommand.Parameters != null)
                {
                    // ActionScript için parametre türlerini dönüştür;
                    foreach (var paramKey in processedCommand.Parameters.Keys.ToList())
                    {
                        var value = processedCommand.Parameters[paramKey];
                        if (value is bool boolValue)
                        {
                            processedCommand.Parameters[paramKey] = boolValue ? "true" : "false";
                        }
                    }
                }

                processed.Add(processedCommand);
            }

            await Task.CompletedTask;
            return processed;
        }

        private async Task<List<PhotoshopCommand>> ProcessCommandsForVisualBasicAsync(
            List<PhotoshopCommand> commands,
            VisualBasicOptions options)
        {
            var processed = new List<PhotoshopCommand>();

            foreach (var command in commands)
            {
                var processedCommand = command.Clone();

                // Visual Basic'e özel işlemler;
                if (processedCommand.Parameters != null)
                {
                    // Visual Basic için parametre türlerini dönüştür;
                    foreach (var paramKey in processedCommand.Parameters.Keys.ToList())
                    {
                        var value = processedCommand.Parameters[paramKey];
                        if (value is bool boolValue)
                        {
                            processedCommand.Parameters[paramKey] = boolValue ? "True" : "False";
                        }
                    }
                }

                processed.Add(processedCommand);
            }

            await Task.CompletedTask;
            return processed;
        }

        private string AddErrorHandling(string script, ErrorHandlingOptions options)
        {
            if (options == null || !options.Enabled)
                return script;

            var errorHandlingCode = new StringBuilder();

            errorHandlingCode.AppendLine("// Error handling section");
            errorHandlingCode.AppendLine("try {");

            if (options.CatchSpecificErrors)
            {
                errorHandlingCode.AppendLine("    // Main script execution");
                errorHandlingCode.AppendLine("    " + script.Replace("\n", "\n    "));
            }
            else;
            {
                errorHandlingCode.AppendLine(script);
            }

            errorHandlingCode.AppendLine("}");
            errorHandlingCode.AppendLine("catch (e) {");
            errorHandlingCode.AppendLine($"    alert(\"Script error: \" + e.message);");

            if (options.LogToConsole)
            {
                errorHandlingCode.AppendLine("    console.error(\"Photoshop script error:\", e);");
            }

            if (options.RollbackOnError)
            {
                errorHandlingCode.AppendLine("    // Rollback changes");
                errorHandlingCode.AppendLine("    app.activeDocument.selection.deselect();");
                errorHandlingCode.AppendLine("    app.activeDocument.historyStates[0].goTo();");
            }

            errorHandlingCode.AppendLine("}");
            errorHandlingCode.AppendLine("finally {");
            errorHandlingCode.AppendLine("    // Cleanup");
            errorHandlingCode.AppendLine("    app.redraw();");
            errorHandlingCode.AppendLine("}");

            return errorHandlingCode.ToString();
        }

        private string AddComments(string script, List<PhotoshopCommand> commands, JavaScriptOptions options)
        {
            var commentedScript = new StringBuilder();

            // Başlık yorumu;
            commentedScript.AppendLine("/*");
            commentedScript.AppendLine($" * Photoshop Script");
            commentedScript.AppendLine($" * Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            commentedScript.AppendLine($" * Commands: {commands.Count}");
            commentedScript.AppendLine($" */");
            commentedScript.AppendLine();

            // Komut başına yorumlar;
            var lines = script.Split('\n');
            var commandIndex = 0;

            foreach (var line in lines)
            {
                if (line.TrimStart().StartsWith("//"))
                {
                    commentedScript.AppendLine(line);
                }
                else if (!string.IsNullOrWhiteSpace(line))
                {
                    // Komut satırına yorum ekle;
                    if (commandIndex < commands.Count)
                    {
                        commentedScript.AppendLine($"// {commands[commandIndex].Name}: {commands[commandIndex].Description}");
                        commandIndex++;
                    }
                    commentedScript.AppendLine(line);
                }
                else;
                {
                    commentedScript.AppendLine(line);
                }
            }

            return commentedScript.ToString();
        }

        private string AddJavaScriptHeader(string script, JavaScriptOptions options, int commandCount)
        {
            var header = new StringBuilder();

            header.AppendLine("/*==========================================");
            header.AppendLine("  Photoshop JavaScript Script");
            header.AppendLine("==========================================*/");
            header.AppendLine();
            header.AppendLine("// Script Information");
            header.AppendLine($"// Generated by: {options.GeneratorName}");
            header.AppendLine($"// Version: {options.Version}");
            header.AppendLine($"// Command Count: {commandCount}");
            header.AppendLine($"// Target Photoshop: {options.TargetPhotoshopVersion}");
            header.AppendLine();
            header.AppendLine("// Main Script");
            header.AppendLine();

            return header.ToString() + script;
        }

        private string AddActionScriptErrorHandling(string script, ErrorHandlingOptions options)
        {
            // ActionScript için hata yönetimi;
            var errorScript = new StringBuilder();

            errorScript.AppendLine("on error resume next");
            errorScript.AppendLine();
            errorScript.AppendLine(script);
            errorScript.AppendLine();

            if (options?.Enabled == true)
            {
                errorScript.AppendLine("if err.number <> 0 then");
                errorScript.AppendLine($"    msgbox \"Error: \" & err.description, vbCritical, \"Script Error\"");
                errorScript.AppendLine("end if");
            }

            return errorScript.ToString();
        }

        private string AddActionScriptComments(string script, List<PhotoshopCommand> commands, ActionScriptOptions options)
        {
            var commentedScript = new StringBuilder();

            commentedScript.AppendLine("'==========================================");
            commentedScript.AppendLine("' Photoshop ActionScript");
            commentedScript.AppendLine("'==========================================");
            commentedScript.AppendLine();
            commentedScript.AppendLine("' Commands:");

            foreach (var cmd in commands)
            {
                commentedScript.AppendLine($"'   - {cmd.Name}: {cmd.Description}");
            }

            commentedScript.AppendLine();
            commentedScript.AppendLine(script);

            return commentedScript.ToString();
        }

        private string AddActionScriptHeader(string script, ActionScriptOptions options, int commandCount)
        {
            var header = new StringBuilder();

            header.AppendLine("'==========================================");
            header.AppendLine("' Photoshop Action Script");
            header.AppendLine($"' Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"' Commands: {commandCount}");
            header.AppendLine("'==========================================");
            header.AppendLine();

            return header.ToString() + script;
        }

        private string AddVisualBasicErrorHandling(string script, ErrorHandlingOptions options)
        {
            var errorScript = new StringBuilder();

            errorScript.AppendLine("On Error GoTo ErrorHandler");
            errorScript.AppendLine();
            errorScript.AppendLine(script);
            errorScript.AppendLine();
            errorScript.AppendLine("Exit Sub");
            errorScript.AppendLine();
            errorScript.AppendLine("ErrorHandler:");
            errorScript.AppendLine("    MsgBox \"Error \" & Err.Number & \": \" & Err.Description, vbCritical");
            errorScript.AppendLine("    Resume Next");

            return errorScript.ToString();
        }

        private string AddVisualBasicComments(string script, List<PhotoshopCommand> commands, VisualBasicOptions options)
        {
            var commentedScript = new StringBuilder();

            commentedScript.AppendLine("'==========================================");
            commentedScript.AppendLine("' Photoshop Visual Basic Script");
            commentedScript.AppendLine("'==========================================");
            commentedScript.AppendLine();
            commentedScript.AppendLine("' Command List:");

            foreach (var cmd in commands)
            {
                commentedScript.AppendLine($"'   • {cmd.Name}");
                if (!string.IsNullOrEmpty(cmd.Description))
                {
                    commentedScript.AppendLine($"'     {cmd.Description}");
                }
            }

            commentedScript.AppendLine();
            commentedScript.AppendLine(script);

            return commentedScript.ToString();
        }

        private string AddVisualBasicHeader(string script, VisualBasicOptions options, int commandCount)
        {
            var header = new StringBuilder();

            header.AppendLine("Option Explicit");
            header.AppendLine();
            header.AppendLine("' Script Information");
            header.AppendLine($"' Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            header.AppendLine($"' Command Count: {commandCount}");
            header.AppendLine($"' Script Version: {options.Version}");
            header.AppendLine();

            return header.ToString() + script;
        }

        private async Task<string> ValidateJavaScriptSyntaxAsync(string script)
        {
            // JavaScript sözdizimi doğrulama;
            // Basit doğrulamalar;

            // Açık/kapalı parantez kontrolü;
            var openBraces = script.Count(c => c == '{');
            var closeBraces = script.Count(c => c == '}');

            if (openBraces != closeBraces)
            {
                _logger.Warn($"JavaScript brace mismatch: {openBraces} open, {closeBraces} close");
            }

            // Temel anahtar kelime kontrolü;
            var requiredKeywords = new[] { "app", "activeDocument" };
            foreach (var keyword in requiredKeywords)
            {
                if (!script.Contains(keyword))
                {
                    _logger.Debug($"JavaScript missing required keyword: {keyword}");
                }
            }

            await Task.CompletedTask;
            return script;
        }

        private string ComputeContentHash(string content)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(content));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private async Task HandleGenerationFailureAsync(Guid generationId, DateTime startTime, Exception exception)
        {
            await _eventBus.PublishAsync(new ScriptGenerationFailedEvent;
            {
                GenerationId = generationId,
                ErrorMessage = exception.Message,
                ExceptionType = exception.GetType().Name,
                Duration = DateTime.UtcNow - startTime,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void ValidateBasicScriptProperties(GeneratedScript script, ScriptValidationResult result)
        {
            // İsim kontrolü;
            if (string.IsNullOrWhiteSpace(script.Name))
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.InvalidName,
                    Message = "Script name cannot be empty"
                });
            }

            // İçerik kontrolü;
            if (string.IsNullOrWhiteSpace(script.Content))
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.EmptyContent,
                    Message = "Script content cannot be empty"
                });
            }

            // Dil kontrolü;
            if (!Enum.IsDefined(typeof(ScriptLanguage), script.Language))
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.UnsupportedLanguage,
                    Message = $"Unsupported script language: {script.Language}"
                });
            }

            result.IsValid = !result.Errors.Any();
        }

        private async Task ValidateScriptSyntaxAsync(GeneratedScript script, ScriptValidationResult result)
        {
            try
            {
                var syntaxResult = await _scriptValidator.ValidateSyntaxAsync(script.Content, script.Language);

                if (!syntaxResult.IsValid)
                {
                    foreach (var error in syntaxResult.Errors)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorType = ValidationErrorType.SyntaxError,
                            Message = error.Message,
                            LineNumber = error.LineNumber,
                            ColumnNumber = error.ColumnNumber;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Errors.Add(new ValidationError;
                {
                    ErrorType = ValidationErrorType.ValidationFailed,
                    Message = $"Syntax validation failed: {ex.Message}"
                });
            }
        }

        private async Task ValidateScriptSemanticsAsync(GeneratedScript script, ScriptValidationResult result)
        {
            try
            {
                // Photoshop API uyumluluğunu kontrol et;
                var semanticResult = await _scriptValidator.ValidateSemanticsAsync(script.Content, script.Language);

                if (!semanticResult.IsValid)
                {
                    foreach (var warning in semanticResult.Warnings)
                    {
                        result.Warnings.Add(warning);
                    }

                    foreach (var error in semanticResult.Errors)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorType = ValidationErrorType.SemanticError,
                            Message = error;
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Semantic validation failed: {ex.Message}");
            }
        }

        private async Task ValidateScriptPerformanceAsync(GeneratedScript script, ScriptValidationResult result)
        {
            try
            {
                // Performans analizi;
                var performanceMetrics = await _scriptValidator.AnalyzePerformanceAsync(script.Content, script.Language);

                result.Metadata["PerformanceMetrics"] = performanceMetrics;

                // Uyarıları ekle;
                if (performanceMetrics.EstimatedExecutionTime > TimeSpan.FromSeconds(30))
                {
                    result.Warnings.Add($"Script execution time is long: {performanceMetrics.EstimatedExecutionTime.TotalSeconds:F1}s");
                }

                if (performanceMetrics.MemoryUsageEstimate > 100 * 1024 * 1024) // 100MB;
                {
                    result.Warnings.Add($"High memory usage estimated: {performanceMetrics.MemoryUsageEstimate / 1024 / 1024:F1}MB");
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Performance validation failed: {ex.Message}");
            }
        }

        private async Task ValidateScriptSecurityAsync(GeneratedScript script, ScriptValidationResult result)
        {
            try
            {
                // Güvenlik kontrolleri;
                var securityIssues = await _scriptValidator.DetectSecurityIssuesAsync(script.Content, script.Language);

                foreach (var issue in securityIssues)
                {
                    result.SecurityIssues.Add(issue);

                    if (issue.Severity >= SecuritySeverity.High)
                    {
                        result.Errors.Add(new ValidationError;
                        {
                            ErrorType = ValidationErrorType.SecurityIssue,
                            Message = $"Security issue: {issue.Description}",
                            Severity = issue.Severity.ToString()
                        });
                    }
                    else;
                    {
                        result.Warnings.Add($"Security warning: {issue.Description}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"Security validation failed: {ex.Message}");
            }
        }

        private string GenerateCodeSnippet(string content, int lineCount)
        {
            var lines = content.Split('\n');
            var snippet = string.Join("\n", lines.Take(lineCount));

            if (lines.Length > lineCount)
            {
                snippet += $"\n\n... ({lines.Length - lineCount} more lines)";
            }

            return snippet;
        }

        private string GenerateStructurePreview(GeneratedScript script)
        {
            var preview = new StringBuilder();

            preview.AppendLine($"Script: {script.Name}");
            preview.AppendLine($"Language: {script.Language}");
            preview.AppendLine($"Size: {script.Content.Length} characters");
            preview.AppendLine($"Generated: {script.GeneratedTime:yyyy-MM-dd HH:mm:ss}");
            preview.AppendLine();

            // Yapısal analiz;
            var lines = script.Content.Split('\n');
            preview.AppendLine($"Lines: {lines.Length}");
            preview.AppendLine($"Functions: {CountOccurrences(script.Content, "function ")}");
            preview.AppendLine($"Variables: {CountOccurrences(script.Content, "var ")}");
            preview.AppendLine($"Comments: {CountOccurrences(script.Content, "//") + CountOccurrences(script.Content, "/*")}");

            return preview.ToString();
        }

        private int CountOccurrences(string text, string pattern)
        {
            int count = 0;
            int index = 0;

            while ((index = text.IndexOf(pattern, index, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                count++;
                index += pattern.Length;
            }

            return count;
        }

        private async Task<string> GenerateCommandsPreviewAsync(GeneratedScript script)
        {
            if (script.Metadata.TryGetValue("OriginalCommandCount", out var countObj) &&
                countObj is int commandCount)
            {
                return $"Commands in script: {commandCount}\n" +
                       $"Language: {script.Language}\n" +
                       $"Output type: {script.OutputType}";
            }

            await Task.CompletedTask;
            return "Command information not available";
        }

        private string GenerateStatisticsPreview(GeneratedScript script)
        {
            var stats = new StringBuilder();

            stats.AppendLine("=== Script Statistics ===");
            stats.AppendLine($"Name: {script.Name}");
            stats.AppendLine($"ID: {script.Id}");
            stats.AppendLine($"Language: {script.Language}");
            stats.AppendLine($"Size: {script.Content.Length} characters");
            stats.AppendLine($"Lines: {script.Content.Split('\n').Length}");
            stats.AppendLine($"Generated: {script.GeneratedTime:yyyy-MM-dd HH:mm:ss}");
            stats.AppendLine($"Generation time: {script.GenerationTime.TotalMilliseconds:F0}ms");

            if (script.Optimized)
            {
                stats.AppendLine($"Optimized: Yes");
                if (script.OptimizationResult != null)
                {
                    stats.AppendLine($"Size reduction: {script.OptimizationResult.ReductionPercentage:F2}%");
                }
            }

            if (script.ValidationResult != null)
            {
                stats.AppendLine($"Valid: {script.ValidationResult.IsValid}");
                stats.AppendLine($"Errors: {script.ValidationResult.Errors.Count}");
                stats.AppendLine($"Warnings: {script.ValidationResult.Warnings.Count}");
            }

            return stats.ToString();
        }

        private ScriptTemplate CreateBatchProcessingTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "BatchImageProcessor",
                Description = "Process multiple images with the same operations",
                Category = "Batch Processing",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "OpenFolder",
                        Type = CommandType.OpenFolder,
                        Description = "Open source folder"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ProcessImages",
                        Type = CommandType.BatchProcess,
                        Description = "Process all images in folder"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "SaveResults",
                        Type = CommandType.SaveFolder,
                        Description = "Save processed images"
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["sourceFolder"] = new ParameterDefinition;
                    {
                        Type = ParameterType.DirectoryPath,
                        Description = "Source folder path",
                        Required = true;
                    },
                    ["targetFolder"] = new ParameterDefinition;
                    {
                        Type = ParameterType.DirectoryPath,
                        Description = "Target folder path",
                        Required = true;
                    }
                }
            };
        }

        private ScriptTemplate CreateImageResizeTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "ImageResizer",
                Description = "Resize images to specified dimensions",
                Category = "Resizing",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "OpenImage",
                        Type = CommandType.OpenFile,
                        Description = "Open image file"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ResizeImage",
                        Type = CommandType.Resize,
                        Description = "Resize to target dimensions",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Width"] = "{{targetWidth}}",
                            ["Height"] = "{{targetHeight}}",
                            ["ResampleMethod"] = "Bicubic"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "SaveResized",
                        Type = CommandType.SaveFile,
                        Description = "Save resized image"
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["targetWidth"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "Target width in pixels",
                        Required = true,
                        MinValue = 10,
                        MaxValue = 10000;
                    },
                    ["targetHeight"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "Target height in pixels",
                        Required = true,
                        MinValue = 10,
                        MaxValue = 10000;
                    }
                }
            };
        }

        private ScriptTemplate CreateWatermarkTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "WatermarkAdder",
                Description = "Add watermark to images",
                Category = "Watermarking",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "OpenImage",
                        Type = CommandType.OpenFile,
                        Description = "Open main image"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "OpenWatermark",
                        Type = CommandType.OpenFile,
                        Description = "Open watermark image"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "PlaceWatermark",
                        Type = CommandType.PlaceWatermark,
                        Description = "Place watermark on image",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Opacity"] = "{{opacity}}",
                            ["Position"] = "{{position}}"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "SaveWatermarked",
                        Type = CommandType.SaveFile,
                        Description = "Save watermarked image"
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["watermarkPath"] = new ParameterDefinition;
                    {
                        Type = ParameterType.FilePath,
                        Description = "Watermark image path",
                        Required = true;
                    },
                    ["opacity"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "Watermark opacity (0-100)",
                        Required = true,
                        MinValue = 0,
                        MaxValue = 100,
                        DefaultValue = 50;
                    }
                }
            };
        }

        private ScriptTemplate CreateColorCorrectionTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "ColorCorrector",
                Description = "Automated color correction",
                Category = "Color Correction",
                Version = "1.0.0",
                OutputType = ScriptOutputType.ActionScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "AutoLevels",
                        Type = CommandType.AdjustColor,
                        Description = "Apply auto levels",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Adjustment"] = "Levels",
                            ["Auto"] = true;
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "AutoContrast",
                        Type = CommandType.AdjustColor,
                        Description = "Apply auto contrast"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "AutoColor",
                        Type = CommandType.AdjustColor,
                        Description = "Apply auto color"
                    }
                }
            };
        }

        private ScriptTemplate CreateFilterApplicationTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "FilterApplier",
                Description = "Apply multiple filters to image",
                Category = "Filter Effects",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "ApplySharpen",
                        Type = CommandType.ApplyFilter,
                        Description = "Apply sharpen filter",
                        Parameters = new Dictionary<string, object>
                        {
                            ["FilterName"] = "Sharpen",
                            ["Amount"] = "{{sharpenAmount}}"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ApplyBlur",
                        Type = CommandType.ApplyFilter,
                        Description = "Apply Gaussian blur",
                        Parameters = new Dictionary<string, object>
                        {
                            ["FilterName"] = "GaussianBlur",
                            ["Radius"] = "{{blurRadius}}"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ApplyNoise",
                        Type = CommandType.ApplyFilter,
                        Description = "Apply noise reduction",
                        Parameters = new Dictionary<string, object>
                        {
                            ["FilterName"] = "ReduceNoise",
                            ["Strength"] = "{{noiseStrength}}"
                        }
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["sharpenAmount"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "Sharpen amount (0-500)",
                        DefaultValue = 100,
                        MinValue = 0,
                        MaxValue = 500;
                    },
                    ["blurRadius"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Double,
                        Description = "Blur radius (0.1-10.0)",
                        DefaultValue = 1.0,
                        MinValue = 0.1,
                        MaxValue = 10.0;
                    }
                }
            };
        }

        private ScriptTemplate CreateExportTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "MultiFormatExporter",
                Description = "Export image in multiple formats",
                Category = "Export",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "ExportJPEG",
                        Type = CommandType.Export,
                        Description = "Export as JPEG",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Format"] = "JPEG",
                            ["Quality"] = "{{jpegQuality}}"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ExportPNG",
                        Type = CommandType.Export,
                        Description = "Export as PNG",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Format"] = "PNG"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ExportWebP",
                        Type = CommandType.Export,
                        Description = "Export as WebP",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Format"] = "WebP",
                            ["Quality"] = "{{webpQuality}}"
                        }
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["jpegQuality"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "JPEG quality (1-100)",
                        DefaultValue = 90,
                        MinValue = 1,
                        MaxValue = 100;
                    },
                    ["webpQuality"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "WebP quality (1-100)",
                        DefaultValue = 80,
                        MinValue = 1,
                        MaxValue = 100;
                    }
                }
            };
        }

        private ScriptTemplate CreateLayerManagementTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "LayerManager",
                Description = "Manage layers in Photoshop document",
                Category = "Layers",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "CreateLayer",
                        Type = CommandType.CreateLayer,
                        Description = "Create new layer"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "DuplicateLayer",
                        Type = CommandType.DuplicateLayer,
                        Description = "Duplicate current layer"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "MergeLayers",
                        Type = CommandType.MergeLayers,
                        Description = "Merge visible layers"
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ApplyLayerStyle",
                        Type = CommandType.ApplyLayerStyle,
                        Description = "Apply layer style",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Style"] = "{{layerStyle}}"
                        }
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["layerStyle"] = new ParameterDefinition;
                    {
                        Type = ParameterType.String,
                        Description = "Layer style to apply",
                        DefaultValue = "Drop Shadow"
                    }
                }
            };
        }

        private ScriptTemplate CreateTextEffectTemplate()
        {
            return new ScriptTemplate;
            {
                Name = "TextEffectCreator",
                Description = "Create text with effects",
                Category = "Text",
                Version = "1.0.0",
                OutputType = ScriptOutputType.JavaScript,
                Commands = new List<PhotoshopCommand>
                {
                    new PhotoshopCommand;
                    {
                        Name = "CreateTextLayer",
                        Type = CommandType.CreateText,
                        Description = "Create text layer",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Text"] = "{{textContent}}",
                            ["Font"] = "{{fontName}}",
                            ["Size"] = "{{fontSize}}"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "ApplyTextEffect",
                        Type = CommandType.ApplyTextEffect,
                        Description = "Apply text effect",
                        Parameters = new Dictionary<string, object>
                        {
                            ["Effect"] = "{{textEffect}}"
                        }
                    },
                    new PhotoshopCommand;
                    {
                        Name = "RasterizeText",
                        Type = CommandType.RasterizeLayer,
                        Description = "Rasterize text layer"
                    }
                },
                RequiredParameters = new Dictionary<string, ParameterDefinition>
                {
                    ["textContent"] = new ParameterDefinition;
                    {
                        Type = ParameterType.String,
                        Description = "Text content",
                        Required = true;
                    },
                    ["fontName"] = new ParameterDefinition;
                    {
                        Type = ParameterType.String,
                        Description = "Font name",
                        DefaultValue = "Arial"
                    },
                    ["fontSize"] = new ParameterDefinition;
                    {
                        Type = ParameterType.Integer,
                        Description = "Font size in points",
                        DefaultValue = 72,
                        MinValue = 6,
                        MaxValue = 500;
                    }
                }
            };
        }

        #endregion;

        #region IDisposable Implementation;

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
                    // Dil üreticilerini temizle;
                    foreach (var generator in _languageGenerators.Values)
                    {
                        if (generator is IDisposable disposable)
                        {
                            disposable.Dispose();
                        }
                    }
                    _languageGenerators.Clear();

                    // Önbelleği temizle;
                    _templateCache.Clear();

                    _logger.Info("ScriptGenerator disposed");
                }

                _disposed = true;
            }
        }

        ~ScriptGenerator()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Photoshop komutu;
    /// </summary>
    public class PhotoshopCommand;
    {
        public string Name { get; set; }
        public CommandType Type { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public CommandOptions Options { get; set; } = new CommandOptions();
        public int Order { get; set; }
        public bool Enabled { get; set; } = true;

        public PhotoshopCommand Clone()
        {
            return new PhotoshopCommand;
            {
                Name = this.Name,
                Type = this.Type,
                Description = this.Description,
                Parameters = this.Parameters != null;
                    ? new Dictionary<string, object>(this.Parameters)
                    : new Dictionary<string, object>(),
                Options = this.Options?.Clone(),
                Order = this.Order,
                Enabled = this.Enabled;
            };
        }
    }

    /// <summary>
    /// Komut seçenekleri;
    /// </summary>
    public class CommandOptions;
    {
        public bool ContinueOnError { get; set; } = true;
        public int RetryCount { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public Dictionary<string, object> CustomOptions { get; set; } = new Dictionary<string, object>();

        public CommandOptions Clone()
        {
            return new CommandOptions;
            {
                ContinueOnError = this.ContinueOnError,
                RetryCount = this.RetryCount,
                Timeout = this.Timeout,
                CustomOptions = new Dictionary<string, object>(this.CustomOptions)
            };
        }
    }

    /// <summary>
    /// Betik oluşturma seçenekleri;
    /// </summary>
    public class ScriptGenerationOptions;
    {
        public string ScriptName { get; set; }
        public string Description { get; set; }
        public ScriptOutputType OutputType { get; set; } = ScriptOutputType.JavaScript;
        public bool Optimize { get; set; } = true;
        public bool Validate { get; set; } = true;
        public bool FormatCode { get; set; } = true;
        public bool ThrowOnValidationError { get; set; } = true;
        public JavaScriptOptions JavaScriptOptions { get; set; } = new JavaScriptOptions();
        public ActionScriptOptions ActionScriptOptions { get; set; } = new ActionScriptOptions();
        public VisualBasicOptions VisualBasicOptions { get; set; } = new VisualBasicOptions();
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Oluşturulmuş betik;
    /// </summary>
    public class GeneratedScript;
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Content { get; set; }
        public ScriptLanguage Language { get; set; }
        public ScriptOutputType OutputType { get; set; }
        public DateTime GeneratedTime { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public bool Optimized { get; set; }
        public string TemplateName { get; set; }
        public string TemplateVersion { get; set; }
        public Dictionary<string, object> TemplateParameters { get; set; } = new Dictionary<string, object>();
        public string TemplateContent { get; set; }
        public OptimizationReport OptimizationReport { get; set; }
        public OptimizationResult OptimizationResult { get; set; }
        public ScriptValidationResult ValidationResult { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public GeneratedScript Clone()
        {
            return new GeneratedScript;
            {
                Id = this.Id,
                Name = this.Name,
                Description = this.Description,
                Content = this.Content,
                Language = this.Language,
                OutputType = this.OutputType,
                GeneratedTime = this.GeneratedTime,
                GenerationTime = this.GenerationTime,
                Optimized = this.Optimized,
                TemplateName = this.TemplateName,
                TemplateVersion = this.TemplateVersion,
                TemplateParameters = new Dictionary<string, object>(this.TemplateParameters),
                TemplateContent = this.TemplateContent,
                OptimizationReport = this.OptimizationReport?.Clone(),
                OptimizationResult = this.OptimizationResult?.Clone(),
                ValidationResult = this.ValidationResult?.Clone(),
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Betik şablonu;
    /// </summary>
    public class ScriptTemplate;
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Content { get; set; }
        public List<PhotoshopCommand> Commands { get; set; } = new List<PhotoshopCommand>();
        public ScriptOutputType OutputType { get; set; } = ScriptOutputType.JavaScript;
        public string Version { get; set; } = "1.0.0";
        public string Author { get; set; }
        public Dictionary<string, ParameterDefinition> RequiredParameters { get; set; } = new Dictionary<string, ParameterDefinition>();
        public Dictionary<string, object> DefaultParameters { get; set; } = new Dictionary<string, object>();
        public JavaScriptOptions JavaScriptOptions { get; set; } = new JavaScriptOptions();
        public ActionScriptOptions ActionScriptOptions { get; set; } = new ActionScriptOptions();
        public VisualBasicOptions VisualBasicOptions { get; set; } = new VisualBasicOptions();
        public List<string> Tags { get; set; } = new List<string>();
        public DateTime Created { get; set; } = DateTime.UtcNow;
        public DateTime Modified { get; set; } = DateTime.UtcNow;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Parametre tanımı;
    /// </summary>
    public class ParameterDefinition;
    {
        public string Name { get; set; }
        public ParameterType Type { get; set; }
        public string Description { get; set; }
        public bool Required { get; set; }
        public object DefaultValue { get; set; }
        public object MinValue { get; set; }
        public object MaxValue { get; set; }
        public List<object> AllowedValues { get; set; } = new List<object>();
    }

    /// <summary>
    /// Şablon parametreleri;
    /// </summary>
    public class TemplateParameters;
    {
        public string ScriptName { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Values { get; set; } = new Dictionary<string, object>();
        public List<PhotoshopCommand> AdditionalCommands { get; set; } = new List<PhotoshopCommand>();
        public bool Optimize { get; set; } = true;
        public bool Validate { get; set; } = true;
        public bool FormatCode { get; set; } = true;
        public Dictionary<string, object> Options { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// JavaScript seçenekleri;
    /// </summary>
    public class JavaScriptOptions;
    {
        public string GeneratorName { get; set; } = "NEDA Script Generator";
        public string Version { get; set; } = "1.0.0";
        public string TargetPhotoshopVersion { get; set; } = "CC 2023";
        public bool AddHeader { get; set; } = true;
        public bool AddComments { get; set; } = true;
        public bool UseStrictMode { get; set; } = true;
        public ErrorHandlingOptions ErrorHandling { get; set; } = new ErrorHandlingOptions();
        public int DefaultSaveQuality { get; set; } = 90;
        public string DefaultSaveFormat { get; set; } = "JPEG";
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// ActionScript seçenekleri;
    /// </summary>
    public class ActionScriptOptions;
    {
        public bool AddHeader { get; set; } = true;
        public bool AddComments { get; set; } = true;
        public ErrorHandlingOptions ErrorHandling { get; set; } = new ErrorHandlingOptions();
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Visual Basic seçenekleri;
    /// </summary>
    public class VisualBasicOptions;
    {
        public string Version { get; set; } = "1.0.0";
        public bool AddHeader { get; set; } = true;
        public bool AddComments { get; set; } = true;
        public ErrorHandlingOptions ErrorHandling { get; set; } = new ErrorHandlingOptions();
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Hata yönetimi seçenekleri;
    /// </summary>
    public class ErrorHandlingOptions;
    {
        public bool Enabled { get; set; } = true;
        public bool CatchSpecificErrors { get; set; } = true;
        public bool LogToConsole { get; set; } = true;
        public bool RollbackOnError { get; set; }
        public string CustomErrorMessage { get; set; }
        public Dictionary<string, object> AdditionalOptions { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Optimizasyon raporu;
    /// </summary>
    public class OptimizationReport;
    {
        public int OriginalCommandCount { get; set; }
        public int OptimizedCommandCount { get; set; }
        public int RemovedCommands { get; set; }
        public double OptimizationRate { get; set; }
        public List<string> RemovedCommandNames { get; set; } = new List<string>();
        public List<string> MergedCommands { get; set; } = new List<string>();
        public DateTime OptimizationTime { get; set; }
        public Dictionary<string, object> Details { get; set; } = new Dictionary<string, object>();

        public OptimizationReport Clone()
        {
            return new OptimizationReport;
            {
                OriginalCommandCount = this.OriginalCommandCount,
                OptimizedCommandCount = this.OptimizedCommandCount,
                RemovedCommands = this.RemovedCommands,
                OptimizationRate = this.OptimizationRate,
                RemovedCommandNames = new List<string>(this.RemovedCommandNames),
                MergedCommands = new List<string>(this.MergedCommands),
                OptimizationTime = this.OptimizationTime,
                Details = new Dictionary<string, object>(this.Details)
            };
        }
    }

    /// <summary>
    /// Optimizasyon sonucu;
    /// </summary>
    public class OptimizationResult;
    {
        public int OriginalSize { get; set; }
        public int OptimizedSize { get; set; }
        public int SizeReduction { get; set; }
        public double ReductionPercentage { get; set; }
        public string OriginalHash { get; set; }
        public string OptimizedHash { get; set; }
        public DateTime OptimizationTime { get; set; }
        public List<string> AppliedOptimizations { get; set; } = new List<string>();
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();

        public OptimizationResult Clone()
        {
            return new OptimizationResult;
            {
                OriginalSize = this.OriginalSize,
                OptimizedSize = this.OptimizedSize,
                SizeReduction = this.SizeReduction,
                ReductionPercentage = this.ReductionPercentage,
                OriginalHash = this.OriginalHash,
                OptimizedHash = this.OptimizedHash,
                OptimizationTime = this.OptimizationTime,
                AppliedOptimizations = new List<string>(this.AppliedOptimizations),
                Metrics = new Dictionary<string, object>(this.Metrics)
            };
        }
    }

    /// <summary>
    /// Betik doğrulama sonucu;
    /// </summary>
    public class ScriptValidationResult;
    {
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public ScriptLanguage Language { get; set; }
        public bool IsValid { get; set; }
        public List<ValidationError> Errors { get; set; } = new List<ValidationError>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<SecurityIssue> SecurityIssues { get; set; } = new List<SecurityIssue>();
        public DateTime ValidationTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();

        public ScriptValidationResult Clone()
        {
            return new ScriptValidationResult;
            {
                ScriptId = this.ScriptId,
                ScriptName = this.ScriptName,
                Language = this.Language,
                IsValid = this.IsValid,
                Errors = this.Errors.Select(e => e.Clone()).ToList(),
                Warnings = new List<string>(this.Warnings),
                SecurityIssues = this.SecurityIssues.Select(s => s.Clone()).ToList(),
                ValidationTime = this.ValidationTime,
                Metadata = new Dictionary<string, object>(this.Metadata)
            };
        }
    }

    /// <summary>
    /// Betik önizlemesi;
    /// </summary>
    public class ScriptPreview;
    {
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public PreviewType PreviewType { get; set; }
        public string Content { get; set; }
        public DateTime GeneratedTime { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Komut türleri;
    /// </summary>
    public enum CommandType;
    {
        OpenFile = 0,
        SaveFile = 1,
        CloseFile = 2,
        Resize = 3,
        Crop = 4,
        Rotate = 5,
        AdjustColor = 6,
        ApplyFilter = 7,
        CreateLayer = 8,
        DuplicateLayer = 9,
        MergeLayers = 10,
        ApplyLayerStyle = 11,
        CreateText = 12,
        ApplyTextEffect = 13,
        RasterizeLayer = 14,
        Export = 15,
        BatchProcess = 16,
        OpenFolder = 17,
        SaveFolder = 18,
        PlaceWatermark = 19,
        RunAction = 20,
        ExecuteScript = 21;
    }

    /// <summary>
    /// Betik çıktı türü;
    /// </summary>
    public enum ScriptOutputType;
    {
        JavaScript = 0,
        ActionScript = 1,
        VisualBasic = 2;
    }

    /// <summary>
    /// Betik dili;
    /// </summary>
    public enum ScriptLanguage;
    {
        JavaScript = 0,
        ActionScript = 1,
        VisualBasic = 2;
    }

    /// <summary>
    /// Parametre türü;
    /// </summary>
    public enum ParameterType;
    {
        String = 0,
        Integer = 1,
        Double = 2,
        Boolean = 3,
        FilePath = 4,
        DirectoryPath = 5,
        Color = 6,
        Enum = 7;
    }

    /// <summary>
    /// Önizleme türü;
    /// </summary>
    public enum PreviewType;
    {
        CodeSnippet = 0,
        Structure = 1,
        Commands = 2,
        Statistics = 3,
        Full = 4;
    }

    /// <summary>
    /// Doğrulama hatası;
    /// </summary>
    public class ValidationError;
    {
        public ValidationErrorType ErrorType { get; set; }
        public string Message { get; set; }
        public int? LineNumber { get; set; }
        public int? ColumnNumber { get; set; }
        public string Severity { get; set; }

        public ValidationError Clone()
        {
            return new ValidationError;
            {
                ErrorType = this.ErrorType,
                Message = this.Message,
                LineNumber = this.LineNumber,
                ColumnNumber = this.ColumnNumber,
                Severity = this.Severity;
            };
        }
    }

    /// <summary>
    /// Doğrulama hata türü;
    /// </summary>
    public enum ValidationErrorType;
    {
        SyntaxError = 0,
        SemanticError = 1,
        SecurityIssue = 2,
        PerformanceIssue = 3,
        InvalidName = 4,
        EmptyContent = 5,
        UnsupportedLanguage = 6,
        ValidationFailed = 7;
    }

    /// <summary>
    /// Güvenlik sorunu;
    /// </summary>
    public class SecurityIssue;
    {
        public string Pattern { get; set; }
        public string Description { get; set; }
        public SecuritySeverity Severity { get; set; }
        public string Recommendation { get; set; }

        public SecurityIssue Clone()
        {
            return new SecurityIssue;
            {
                Pattern = this.Pattern,
                Description = this.Description,
                Severity = this.Severity,
                Recommendation = this.Recommendation;
            };
        }
    }

    /// <summary>
    /// Güvenlik şiddeti;
    /// </summary>
    public enum SecuritySeverity;
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3;
    }

    #endregion;

    #region Event Classes;

    /// <summary>
    /// Betik oluşturuldu olayı;
    /// </summary>
    public class ScriptGeneratedEvent : IEvent;
    {
        public Guid GenerationId { get; set; }
        public string ScriptName { get; set; }
        public ScriptLanguage Language { get; set; }
        public int CommandCount { get; set; }
        public int ScriptSize { get; set; }
        public TimeSpan GenerationTime { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Şablon betiği oluşturuldu olayı;
    /// </summary>
    public class TemplateScriptGeneratedEvent : IEvent;
    {
        public Guid GenerationId { get; set; }
        public string TemplateName { get; set; }
        public string TemplateVersion { get; set; }
        public string ScriptName { get; set; }
        public int ParameterCount { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Betik optimize edildi olayı;
    /// </summary>
    public class ScriptOptimizedEvent : IEvent;
    {
        public Guid ScriptId { get; set; }
        public string ScriptName { get; set; }
        public int OriginalSize { get; set; }
        public int OptimizedSize { get; set; }
        public double ReductionPercentage { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Şablon kaydedildi olayı;
    /// </summary>
    public class TemplateSavedEvent : IEvent;
    {
        public string TemplateName { get; set; }
        public string TemplateVersion { get; set; }
        public string Category { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    /// <summary>
    /// Betik oluşturma başarısız oldu olayı;
    /// </summary>
    public class ScriptGenerationFailedEvent : IEvent;
    {
        public Guid GenerationId { get; set; }
        public string ErrorMessage { get; set; }
        public string ExceptionType { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid EventId { get; set; } = Guid.NewGuid();
    }

    #endregion;

    #region Exception Classes;

    /// <summary>
    /// Betik oluşturma istisnası;
    /// </summary>
    public class ScriptGenerationException : Exception
    {
        public ScriptGenerationException(string message) : base(message) { }
        public ScriptGenerationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Komut doğrulama istisnası;
    /// </summary>
    public class CommandValidationException : Exception
    {
        public CommandValidationException(string message) : base(message) { }
        public CommandValidationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Betik optimizasyonu istisnası;
    /// </summary>
    public class ScriptOptimizationException : Exception
    {
        public ScriptOptimizationException(string message) : base(message) { }
        public ScriptOptimizationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Şablon yönetimi istisnası;
    /// </summary>
    public class TemplateManagementException : Exception
    {
        public TemplateManagementException(string message) : base(message) { }
        public TemplateManagementException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Şablon bulunamadı istisnası;
    /// </summary>
    public class TemplateNotFoundException : Exception
    {
        public TemplateNotFoundException(string message) : base(message) { }
        public TemplateNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Şablon doğrulama istisnası;
    /// </summary>
    public class TemplateValidationException : Exception
    {
        public TemplateValidationException(string message) : base(message) { }
        public TemplateValidationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Parametre doğrulama istisnası;
    /// </summary>
    public class ParameterValidationException : Exception
    {
        public ParameterValidationException(string message) : base(message) { }
        public ParameterValidationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Betik doğrulama istisnası;
    /// </summary>
    public class ScriptValidationException : Exception
    {
        public ScriptValidationException(string message) : base(message) { }
        public ScriptValidationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Betik önizleme istisnası;
    /// </summary>
    public class ScriptPreviewException : Exception
    {
        public ScriptPreviewException(string message) : base(message) { }
        public ScriptPreviewException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    /// <summary>
    /// Dil üreticisi bulunamadı istisnası;
    /// </summary>
    public class LanguageGeneratorNotFoundException : Exception
    {
        public LanguageGeneratorNotFoundException(string message) : base(message) { }
        public LanguageGeneratorNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;

    #region Internal Helper Classes and Interfaces;

    internal interface IScriptLanguageGenerator;
    {
        ScriptLanguage Language { get; }
        Task<string> GenerateAsync(IEnumerable<PhotoshopCommand> commands, object options);
    }

    internal class JavaScriptGenerator : IScriptLanguageGenerator, IDisposable;
    {
        private readonly ILogger _logger;
        private bool _disposed;

        public ScriptLanguage Language => ScriptLanguage.JavaScript;

        public JavaScriptGenerator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> GenerateAsync(IEnumerable<PhotoshopCommand> commands, object options)
        {
            var jsOptions = options as JavaScriptOptions ?? new JavaScriptOptions();
            var commandList = commands.ToList();

            var script = new StringBuilder();

            // Başlık;
            if (jsOptions.UseStrictMode)
            {
                script.AppendLine("\"use strict\";");
                script.AppendLine();
            }

            script.AppendLine("// Photoshop Script");
            script.AppendLine("// Generated by NEDA Script Generator");
            script.AppendLine();

            script.AppendLine("// Main function");
            script.AppendLine("function main() {");
            script.AppendLine("    try {");

            // Komutları ekle;
            foreach (var command in commandList)
            {
                var commandCode = GenerateCommandCode(command, jsOptions);
                script.AppendLine("        " + commandCode);
            }

            script.AppendLine("    }");
            script.AppendLine("    catch (e) {");
            script.AppendLine("        alert(\"Error: \" + e.message);");
            script.AppendLine("        return false;");
            script.AppendLine("    }");
            script.AppendLine("    return true;");
            script.AppendLine("}");
            script.AppendLine();

            // Yardımcı fonksiyonlar;
            script.AppendLine(GenerateHelperFunctions());

            // Çalıştırma;
            script.AppendLine("// Execute script");
            script.AppendLine("if (main()) {");
            script.AppendLine("    alert(\"Script completed successfully!\");");
            script.AppendLine("} else {");
            script.AppendLine("    alert(\"Script failed!\");");
            script.AppendLine("}");

            await Task.CompletedTask;
            return script.ToString();
        }

        private string GenerateCommandCode(PhotoshopCommand command, JavaScriptOptions options)
        {
            var code = new StringBuilder();

            switch (command.Type)
            {
                case CommandType.OpenFile:
                    code.AppendLine($"// Open file: {command.Name}");
                    if (command.Parameters != null && command.Parameters.TryGetValue("FilePath", out var filePath))
                    {
                        code.AppendLine($"app.open(File(\"{filePath}\"));");
                    }
                    break;

                case CommandType.SaveFile:
                    code.AppendLine($"// Save file: {command.Name}");
                    code.AppendLine("var saveOptions = new JPEGSaveOptions();");
                    if (command.Parameters != null && command.Parameters.TryGetValue("Quality", out var quality))
                    {
                        code.AppendLine($"saveOptions.quality = {quality};");
                    }
                    code.AppendLine("app.activeDocument.saveAs(File(app.activeDocument.fullName), saveOptions, true, Extension.LOWERCASE);");
                    break;

                case CommandType.Resize:
                    code.AppendLine($"// Resize: {command.Name}");
                    if (command.Parameters != null)
                    {
                        var width = command.Parameters.GetValueOrDefault("Width", "800");
                        var height = command.Parameters.GetValueOrDefault("Height", "600");
                        var method = command.Parameters.GetValueOrDefault("ResampleMethod", "Bicubic");

                        code.AppendLine($"app.activeDocument.resizeImage({width}, {height}, 72, ResampleMethod.{method});");
                    }
                    break;

                case CommandType.AdjustColor:
                    code.AppendLine($"// Adjust color: {command.Name}");
                    if (command.Parameters != null && command.Parameters.TryGetValue("Adjustment", out var adjustment))
                    {
                        if (adjustment.ToString() == "Levels")
                        {
                            code.AppendLine("app.activeDocument.activeLayer.autoLevels();");
                        }
                        else if (adjustment.ToString() == "Contrast")
                        {
                            code.AppendLine("app.activeDocument.activeLayer.autoContrast();");
                        }
                    }
                    break;

                case CommandType.ApplyFilter:
                    code.AppendLine($"// Apply filter: {command.Name}");
                    if (command.Parameters != null && command.Parameters.TryGetValue("FilterName", out var filterName))
                    {
                        var filterCode = filterName.ToString() switch;
                        {
                            "Sharpen" => "app.activeDocument.activeLayer.smartSharpen(100, 1, 0);",
                            "GaussianBlur" => "app.activeDocument.activeLayer.applyGaussianBlur(1.0);",
                            "ReduceNoise" => "app.activeDocument.activeLayer.applyReduceNoise(5, 0, 7, 0);",
                            _ => $"// Filter '{filterName}' not implemented"
                        };
                        code.AppendLine(filterCode);
                    }
                    break;

                default:
                    code.AppendLine($"// Command '{command.Type}' not implemented");
                    break;
            }

            return code.ToString();
        }

        private string GenerateHelperFunctions()
        {
            var helpers = new StringBuilder();

            helpers.AppendLine("// Helper functions");
            helpers.AppendLine("function getActiveDocument() {");
            helpers.AppendLine("    return app.activeDocument;");
            helpers.AppendLine("}");
            helpers.AppendLine();
            helpers.AppendLine("function showMessage(message) {");
            helpers.AppendLine("    alert(message);");
            helpers.AppendLine("}");
            helpers.AppendLine();
            helpers.AppendLine("function log(message) {");
            helpers.AppendLine("    $.writeln(message);");
            helpers.AppendLine("}");

            return helpers.ToString();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }

    internal class ActionScriptGenerator : IScriptLanguageGenerator;
    {
        private readonly ILogger _logger;

        public ScriptLanguage Language => ScriptLanguage.ActionScript;

        public ActionScriptGenerator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> GenerateAsync(IEnumerable<PhotoshopCommand> commands, object options)
        {
            var asOptions = options as ActionScriptOptions ?? new ActionScriptOptions();
            var commandList = commands.ToList();

            var script = new StringBuilder();

            script.AppendLine("' ActionScript for Photoshop");
            script.AppendLine("' Generated by NEDA Script Generator");
            script.AppendLine();

            script.AppendLine("Option Explicit");
            script.AppendLine();

            script.AppendLine("Dim app");
            script.AppendLine("Set app = CreateObject(\"Photoshop.Application\")");
            script.AppendLine();

            // Komutları ekle;
            foreach (var command in commandList)
            {
                var commandCode = GenerateCommandCode(command, asOptions);
                script.AppendLine(commandCode);
            }

            script.AppendLine();
            script.AppendLine("MsgBox \"Script completed successfully!\", vbInformation");

            await Task.CompletedTask;
            return script.ToString();
        }

        private string GenerateCommandCode(PhotoshopCommand command, ActionScriptOptions options)
        {
            var code = new StringBuilder();

            code.AppendLine($"' {command.Name}: {command.Description}");

            switch (command.Type)
            {
                case CommandType.OpenFile:
                    if (command.Parameters != null && command.Parameters.TryGetValue("FilePath", out var filePath))
                    {
                        code.AppendLine($"app.Open \"{filePath}\"");
                    }
                    break;

                case CommandType.SaveFile:
                    code.AppendLine("app.ActiveDocument.Save");
                    break;

                default:
                    code.AppendLine($"' Command '{command.Type}' not implemented in ActionScript");
                    break;
            }

            return code.ToString();
        }
    }

    internal class VisualBasicGenerator : IScriptLanguageGenerator;
    {
        private readonly ILogger _logger;

        public ScriptLanguage Language => ScriptLanguage.VisualBasic;

        public VisualBasicGenerator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> GenerateAsync(IEnumerable<PhotoshopCommand> commands, object options)
        {
            var vbOptions = options as VisualBasicOptions ?? new VisualBasicOptions();
            var commandList = commands.ToList();

            var script = new StringBuilder();

            script.AppendLine("' Visual Basic Script for Photoshop");
            script.AppendLine("' Generated by NEDA Script Generator");
            script.AppendLine();

            script.AppendLine("Option Explicit");
            script.AppendLine();

            script.AppendLine("Sub Main()");
            script.AppendLine("    On Error GoTo ErrorHandler");
            script.AppendLine();

            script.AppendLine("    Dim app As Object");
            script.AppendLine("    Set app = CreateObject(\"Photoshop.Application\")");
            script.AppendLine();

            // Komutları ekle;
            foreach (var command in commandList)
            {
                var commandCode = GenerateCommandCode(command, vbOptions);
                var lines = commandCode.Split('\n');
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        script.AppendLine("    " + line);
                    }
                }
            }

            script.AppendLine();
            script.AppendLine("    MsgBox \"Script completed successfully!\", vbInformation");
            script.AppendLine("    Exit Sub");
            script.AppendLine();
            script.AppendLine("ErrorHandler:");
            script.AppendLine("    MsgBox \"Error \" & Err.Number & \": \" & Err.Description, vbCritical");
            script.AppendLine("End Sub");

            await Task.CompletedTask;
            return script.ToString();
        }

        private string GenerateCommandCode(PhotoshopCommand command, VisualBasicOptions options)
        {
            var code = new StringBuilder();

            code.AppendLine($"' {command.Name}: {command.Description}");

            switch (command.Type)
            {
                case CommandType.OpenFile:
                    if (command.Parameters != null && command.Parameters.TryGetValue("FilePath", out var filePath))
                    {
                        code.AppendLine($"app.Open \"{filePath}\"");
                    }
                    break;

                case CommandType.SaveFile:
                    code.AppendLine("app.ActiveDocument.Save");
                    break;

                default:
                    code.AppendLine($"' Command '{command.Type}' not implemented in Visual Basic");
                    break;
            }

            return code.ToString();
        }
    }

    internal class ScriptTemplateManager;
    {
        private readonly ILogger _logger;
        private readonly Dictionary<string, ScriptTemplate> _templates = new Dictionary<string, ScriptTemplate>();

        public ScriptTemplateManager(ILogger logger)
        {
            _logger = logger;
        }

        public async Task SaveTemplateAsync(ScriptTemplate template)
        {
            _templates[template.Name] = template;
            _logger.Debug($"Template saved: {template.Name}");
            await Task.CompletedTask;
        }

        public async Task<ScriptTemplate> LoadTemplateAsync(string templateName)
        {
            if (_templates.TryGetValue(templateName, out var template))
            {
                await Task.CompletedTask;
                return template;
            }

            return null;
        }

        public async Task<IEnumerable<ScriptTemplate>> ListTemplatesAsync()
        {
            await Task.CompletedTask;
            return _templates.Values.OrderBy(t => t.Name).ToList();
        }

        public void RegisterBuiltInTemplate(ScriptTemplate template)
        {
            _templates[template.Name] = template;
            _logger.Debug($"Built-in template registered: {template.Name}");
        }
    }

    internal class ScriptOptimizer;
    {
        private readonly ILogger _logger;

        public ScriptOptimizer(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> OptimizeAsync(string script, ScriptLanguage language)
        {
            var optimized = script;

            // Boşlukları temizle;
            optimized = RemoveExtraWhitespace(optimized);

            // Yorumları kaldır (isteğe bağlı)
            optimized = RemoveComments(optimized, language);

            // Gereksiz satırları kaldır;
            optimized = RemoveEmptyLines(optimized);

            // Değişken isimlerini optimize et;
            optimized = OptimizeVariableNames(optimized, language);

            await Task.CompletedTask;
            return optimized;
        }

        public List<string> GetAppliedOptimizations()
        {
            return new List<string>
            {
                "WhitespaceRemoval",
                "CommentRemoval",
                "EmptyLineRemoval",
                "VariableNameOptimization"
            };
        }

        private string RemoveExtraWhitespace(string script)
        {
            // Fazla boşlukları temizle;
            var lines = script.Split('\n');
            var optimizedLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    // Birden fazla boşluğu tek boşluğa indirge;
                    trimmed = System.Text.RegularExpressions.Regex.Replace(trimmed, @"\s+", " ");
                    optimizedLines.Add(trimmed);
                }
            }

            return string.Join("\n", optimizedLines);
        }

        private string RemoveComments(string script, ScriptLanguage language)
        {
            switch (language)
            {
                case ScriptLanguage.JavaScript:
                    // JavaScript yorumlarını kaldır;
                    script = System.Text.RegularExpressions.Regex.Replace(script, @"//.*", "");
                    script = System.Text.RegularExpressions.Regex.Replace(script, @"/\*.*?\*/", "",
                        System.Text.RegularExpressions.RegexOptions.Singleline);
                    break;

                case ScriptLanguage.ActionScript:
                case ScriptLanguage.VisualBasic:
                    // Tek satır yorumlarını kaldır;
                    script = System.Text.RegularExpressions.Regex.Replace(script, @"'.*", "");
                    break;
            }

            return script;
        }

        private string RemoveEmptyLines(string script)
        {
            var lines = script.Split('\n');
            var nonEmptyLines = lines.Where(line => !string.IsNullOrWhiteSpace(line));
            return string.Join("\n", nonEmptyLines);
        }

        private string OptimizeVariableNames(string script, ScriptLanguage language)
        {
            // Basit değişken ismi optimizasyonu;
            // Gerçek implementasyonda daha karmaşık analiz gerekir;
            return script;
        }
    }

    internal class ScriptValidator;
    {
        private readonly ILogger _logger;

        public ScriptValidator(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<SyntaxValidationResult> ValidateSyntaxAsync(string script, ScriptLanguage language)
        {
            var result = new SyntaxValidationResult();

            // Basit sözdizimi kontrolleri;
            switch (language)
            {
                case ScriptLanguage.JavaScript:
                    // Parantez eşleştirmesi;
                    var openBraces = script.Count(c => c == '{');
                    var closeBraces = script.Count(c => c == '}');
                    if (openBraces != closeBraces)
                    {
                        result.Errors.Add(new SyntaxError;
                        {
                            Message = $"Brace mismatch: {openBraces} opening vs {closeBraces} closing",
                            LineNumber = null,
                            ColumnNumber = null;
                        });
                    }
                    break;
            }

            result.IsValid = !result.Errors.Any();
            await Task.CompletedTask;
            return result;
        }

        public async Task<SemanticValidationResult> ValidateSemanticsAsync(string script, ScriptLanguage language)
        {
            var result = new SemanticValidationResult();

            // Photoshop API kontrolleri;
            var requiredFunctions = new[] { "app.", "activeDocument" };

            foreach (var func in requiredFunctions)
            {
                if (!script.Contains(func))
                {
                    result.Warnings.Add($"Missing required Photoshop API reference: {func}");
                }
            }

            await Task.CompletedTask;
            return result;
        }

        public async Task<PerformanceMetrics> AnalyzePerformanceAsync(string script, ScriptLanguage language)
        {
            var metrics = new PerformanceMetrics();

            // Basit performans analizi;
            var lines = script.Split('\n').Length;
            var characters = script.Length;

            metrics.LineCount = lines;
            metrics.CharacterCount = characters;
            metrics.EstimatedExecutionTime = TimeSpan.FromMilliseconds(lines * 10); // Varsayılan: satır başına 10ms;
            metrics.MemoryUsageEstimate = characters * 2; // Basit tahmin;

            await Task.CompletedTask;
            return metrics;
        }

        public async Task<List<SecurityIssue>> DetectSecurityIssuesAsync(string script, ScriptLanguage language)
        {
            var issues = new List<SecurityIssue>();

            // Potansiyel güvenlik sorunları;
            var dangerousPatterns = new Dictionary<string, SecuritySeverity>
            {
                ["eval("] = SecuritySeverity.Critical,
                ["Function("] = SecuritySeverity.High,
                ["exec("] = SecuritySeverity.High,
                ["Shell("] = SecuritySeverity.High;
            };

            foreach (var pattern in dangerousPatterns)
            {
                if (script.Contains(pattern.Key))
                {
                    issues.Add(new SecurityIssue;
                    {
                        Pattern = pattern.Key,
                        Description = $"Potentially dangerous pattern detected: {pattern.Key}",
                        Severity = pattern.Value,
                        Recommendation = $"Avoid using {pattern.Key} in Photoshop scripts"
                    });
                }
            }

            await Task.CompletedTask;
            return issues;
        }
    }

    internal class SyntaxValidationResult;
    {
        public bool IsValid { get; set; }
        public List<SyntaxError> Errors { get; set; } = new List<SyntaxError>();
    }

    internal class SyntaxError;
    {
        public string Message { get; set; }
        public int? LineNumber { get; set; }
        public int? ColumnNumber { get; set; }
    }

    internal class SemanticValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    internal class PerformanceMetrics;
    {
        public int LineCount { get; set; }
        public int CharacterCount { get; set; }
        public TimeSpan EstimatedExecutionTime { get; set; }
        public long MemoryUsageEstimate { get; set; } // bytes;
        public Dictionary<string, object> AdditionalMetrics { get; set; } = new Dictionary<string, object>();
    }

    internal class CodeFormatter;
    {
        private readonly ILogger _logger;

        public CodeFormatter(ILogger logger)
        {
            _logger = logger;
        }

        public async Task<string> FormatAsync(string code, ScriptLanguage language)
        {
            var formatted = code;

            switch (language)
            {
                case ScriptLanguage.JavaScript:
                    formatted = FormatJavaScript(code);
                    break;

                case ScriptLanguage.ActionScript:
                case ScriptLanguage.VisualBasic:
                    formatted = FormatBasicCode(code);
                    break;
            }

            await Task.CompletedTask;
            return formatted;
        }

        private string FormatJavaScript(string code)
        {
            // Basit JavaScript formatlama;
            var lines = code.Split('\n');
            var formattedLines = new List<string>();
            var indentLevel = 0;

            foreach (var line in lines)
            {
                var trimmed = line.Trim();

                if (trimmed.EndsWith("}"))
                {
                    indentLevel = Math.Max(0, indentLevel - 1);
                }

                var indent = new string(' ', indentLevel * 4);
                formattedLines.Add(indent + trimmed);

                if (trimmed.EndsWith("{"))
                {
                    indentLevel++;
                }
            }

            return string.Join("\n", formattedLines);
        }

        private string FormatBasicCode(string code)
        {
            // Basic dil formatlama;
            var lines = code.Split('\n');
            var formattedLines = new List<string>();

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                {
                    formattedLines.Add(trimmed);
                }
            }

            return string.Join("\n", formattedLines);
        }
    }

    #endregion;
}
