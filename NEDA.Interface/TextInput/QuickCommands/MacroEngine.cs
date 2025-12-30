using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
using NEDA.Core.Logging;
using NEDA.Core.Common.Utilities;
using NEDA.Interface.TextInput.QuickCommands;
using NEDA.Automation.Executors;

namespace NEDA.Interface.TextInput.MacroSupport;
{
    /// <summary>
    /// Makro kaydetme, düzenleme ve yürütme motoru;
    /// Karmaşık iş akışlarını otomatikleştirmek için makro sistemi;
    /// </summary>
    public class MacroEngine : IDisposable
    {
        private readonly ILogger _logger;
        private readonly IScriptRunner _scriptRunner;
        private readonly ICommandExecutor _commandExecutor;

        private readonly Dictionary<string, MacroDefinition> _macros;
        private readonly MacroStorage _storage;
        private readonly MacroRecorder _recorder;
        private readonly MacroPlayer _player;

        private readonly SemaphoreSlim _executionLock;
        private readonly object _macrosLock = new object();
        private bool _isRecording;
        private string _currentRecordingName;

        private CancellationTokenSource _globalCancellationTokenSource;

        /// <summary>
        /// Makro motorunu başlatır;
        /// </summary>
        public MacroEngine(
            ILogger logger,
            IScriptRunner scriptRunner,
            ICommandExecutor commandExecutor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scriptRunner = scriptRunner ?? throw new ArgumentNullException(nameof(scriptRunner));
            _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));

            _macros = new Dictionary<string, MacroDefinition>(StringComparer.OrdinalIgnoreCase);
            _storage = new MacroStorage(logger);
            _recorder = new MacroRecorder(logger);
            _player = new MacroPlayer(logger, scriptRunner, commandExecutor);

            _executionLock = new SemaphoreSlim(1, 1);
            _globalCancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("MacroEngine initialized successfully.");
        }

        /// <summary>
        /// Makro motorunu başlatır ve kayıtlı makroları yükler;
        /// </summary>
        public async Task InitializeAsync(string storagePath = null)
        {
            try
            {
                await _storage.InitializeAsync(storagePath);
                await LoadMacrosAsync();

                _logger.LogInformation($"MacroEngine initialized with {_macros.Count} macros.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MacroEngine.");
                throw new MacroEngineException("Initialization failed", ex);
            }
        }

        /// <summary>
        /// Makro kaydını başlatır;
        /// </summary>
        /// <param name="macroName">Makro adı</param>
        /// <param name="description">Makro açıklaması</param>
        /// <param name="category">Kategori</param>
        /// <returns>Kayıt işlemi başarılıysa true</returns>
        public bool StartRecording(string macroName, string description = null, string category = null)
        {
            ValidateMacroName(macroName);

            if (_isRecording)
            {
                throw new MacroEngineException($"Already recording macro: {_currentRecordingName}");
            }

            lock (_macrosLock)
            {
                if (_macros.ContainsKey(macroName))
                {
                    throw new MacroEngineException($"Macro already exists: {macroName}");
                }
            }

            _recorder.StartRecording(macroName);
            _isRecording = true;
            _currentRecordingName = macroName;

            _logger.LogInformation($"Started recording macro: {macroName}");

            return true;
        }

        /// <summary>
        /// Makro kaydını durdurur ve kaydeder;
        /// </summary>
        /// <param name="save">Kaydetme seçeneği</param>
        /// <returns>Kayıtlı makro tanımı</returns>
        public async Task<MacroDefinition> StopRecording(bool save = true)
        {
            if (!_isRecording)
            {
                throw new MacroEngineException("No active recording session.");
            }

            try
            {
                var recordedSteps = _recorder.StopRecording();
                var macroDefinition = new MacroDefinition;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = _currentRecordingName,
                    Description = $"Recorded macro: {_currentRecordingName}",
                    Category = "Recorded",
                    Steps = recordedSteps,
                    Version = "1.0",
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                    ExecutionCount = 0,
                    Metadata = new Dictionary<string, object>
                    {
                        ["RecordingDate"] = DateTime.UtcNow,
                        ["RecordingDuration"] = recordedSteps.Sum(s => s.DelayMs),
                        ["StepCount"] = recordedSteps.Count;
                    }
                };

                if (save)
                {
                    await SaveMacroAsync(macroDefinition);
                    _logger.LogInformation($"Saved recorded macro: {_currentRecordingName} with {recordedSteps.Count} steps");
                }

                return macroDefinition;
            }
            finally
            {
                _isRecording = false;
                _currentRecordingName = null;
            }
        }

        /// <summary>
        /// Makro kaydına adım ekler;
        /// </summary>
        /// <param name="step">Eklenen adım</param>
        public void RecordStep(MacroStep step)
        {
            if (!_isRecording)
            {
                throw new MacroEngineException("Cannot record step - no active recording session.");
            }

            _recorder.RecordStep(step);
            _logger.LogDebug($"Recorded macro step: {step.ActionType} for macro: {_currentRecordingName}");
        }

        /// <summary>
        /// Makro kaydına komut adımı ekler;
        /// </summary>
        /// <param name="command">Komut metni</param>
        /// <param name="parameters">Parametreler</param>
        /// <param name="delayMs">Gecikme (ms)</param>
        public void RecordCommandStep(string command, Dictionary<string, object> parameters = null, int delayMs = 0)
        {
            var step = new MacroStep;
            {
                Id = Guid.NewGuid().ToString(),
                ActionType = MacroActionType.Command,
                Command = command,
                Parameters = parameters ?? new Dictionary<string, object>(),
                DelayMs = delayMs,
                Timestamp = DateTime.UtcNow;
            };

            RecordStep(step);
        }

        /// <summary>
        /// Makro kaydına script adımı ekler;
        /// </summary>
        /// <param name="script">Script içeriği</param>
        /// <param name="language">Script dili</param>
        /// <param name="delayMs">Gecikme (ms)</param>
        public void RecordScriptStep(string script, string language = "javascript", int delayMs = 0)
        {
            var step = new MacroStep;
            {
                Id = Guid.NewGuid().ToString(),
                ActionType = MacroActionType.Script,
                ScriptContent = script,
                ScriptLanguage = language,
                Parameters = new Dictionary<string, object> { ["language"] = language },
                DelayMs = delayMs,
                Timestamp = DateTime.UtcNow;
            };

            RecordStep(step);
        }

        /// <summary>
        /// Makroyu yürütür;
        /// </summary>
        /// <param name="macroName">Makro adı</param>
        /// <param name="parameters">Çalıştırma parametreleri</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Yürütme sonucu</returns>
        public async Task<MacroExecutionResult> ExecuteMacroAsync(
            string macroName,
            Dictionary<string, object> parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateMacroName(macroName);

            MacroDefinition macro;
            lock (_macrosLock)
            {
                if (!_macros.TryGetValue(macroName, out macro))
                {
                    throw new MacroEngineException($"Macro not found: {macroName}");
                }
            }

            await _executionLock.WaitAsync();
            try
            {
                var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _globalCancellationTokenSource.Token;
                ).Token;

                var result = await _player.ExecuteMacroAsync(macro, parameters, linkedToken);

                // Yürütme sayacını güncelle;
                macro.ExecutionCount++;
                macro.LastExecuted = DateTime.UtcNow;

                await _storage.SaveMacroAsync(macro);

                _logger.LogInformation($"Executed macro: {macroName} - Success: {result.IsSuccess}");
                return result;
            }
            finally
            {
                _executionLock.Release();
            }
        }

        /// <summary>
        /// Birden fazla makroyu sırayla yürütür;
        /// </summary>
        /// <param name="macroNames">Makro adları</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        /// <returns>Yürütme sonuçları</returns>
        public async Task<List<MacroExecutionResult>> ExecuteMacroSequenceAsync(
            IEnumerable<string> macroNames,
            CancellationToken cancellationToken = default)
        {
            var results = new List<MacroExecutionResult>();
            var names = macroNames?.ToList() ?? throw new ArgumentNullException(nameof(macroNames));

            if (!names.Any())
            {
                return results;
            }

            _logger.LogInformation($"Executing macro sequence with {names.Count} macros");

            foreach (var macroName in names)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"Macro sequence cancelled before executing: {macroName}");
                    break;
                }

                try
                {
                    var result = await ExecuteMacroAsync(macroName, null, cancellationToken);
                    results.Add(result);

                    if (!result.IsSuccess && result.StopOnFailure)
                    {
                        _logger.LogWarning($"Macro sequence stopped due to failure: {macroName}");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new MacroExecutionResult;
                    {
                        MacroName = macroName,
                        IsSuccess = false,
                        Error = ex.Message,
                        ExecutionTime = TimeSpan.Zero;
                    });

                    _logger.LogError(ex, $"Failed to execute macro in sequence: {macroName}");
                }

                // Makrolar arası gecikme;
                if (names.IndexOf(macroName) < names.Count - 1)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }

            return results;
        }

        /// <summary>
        /// Tüm makroları iptal eder;
        /// </summary>
        public void CancelAllMacros()
        {
            _globalCancellationTokenSource.Cancel();

            // Yeni token source oluştur;
            _globalCancellationTokenSource.Dispose();
            _globalCancellationTokenSource = new CancellationTokenSource();

            _logger.LogInformation("Cancelled all running macros.");
        }

        /// <summary>
        /// Yeni makro oluşturur;
        /// </summary>
        public async Task<MacroDefinition> CreateMacroAsync(MacroDefinition macro)
        {
            if (macro == null)
            {
                throw new ArgumentNullException(nameof(macro));
            }

            ValidateMacroName(macro.Name);
            ValidateMacroSteps(macro.Steps);

            macro.Id = Guid.NewGuid().ToString();
            macro.CreatedDate = DateTime.UtcNow;
            macro.ModifiedDate = DateTime.UtcNow;
            macro.ExecutionCount = 0;

            lock (_macrosLock)
            {
                if (_macros.ContainsKey(macro.Name))
                {
                    throw new MacroEngineException($"Macro already exists: {macro.Name}");
                }

                _macros[macro.Name] = macro;
            }

            await _storage.SaveMacroAsync(macro);
            _logger.LogInformation($"Created new macro: {macro.Name}");

            return macro;
        }

        /// <summary>
        /// Makroyu günceller;
        /// </summary>
        public async Task UpdateMacroAsync(MacroDefinition macro)
        {
            if (macro == null)
            {
                throw new ArgumentNullException(nameof(macro));
            }

            ValidateMacroName(macro.Name);
            ValidateMacroSteps(macro.Steps);

            lock (_macrosLock)
            {
                if (!_macros.ContainsKey(macro.Name))
                {
                    throw new MacroEngineException($"Macro not found: {macro.Name}");
                }

                macro.ModifiedDate = DateTime.UtcNow;
                _macros[macro.Name] = macro;
            }

            await _storage.SaveMacroAsync(macro);
            _logger.LogInformation($"Updated macro: {macro.Name}");
        }

        /// <summary>
        /// Makroyu siler;
        /// </summary>
        public async Task DeleteMacroAsync(string macroName)
        {
            ValidateMacroName(macroName);

            lock (_macrosLock)
            {
                if (!_macros.Remove(macroName))
                {
                    throw new MacroEngineException($"Macro not found: {macroName}");
                }
            }

            await _storage.DeleteMacroAsync(macroName);
            _logger.LogInformation($"Deleted macro: {macroName}");
        }

        /// <summary>
        /// Tüm makroları listeler;
        /// </summary>
        public List<MacroDefinition> GetAllMacros()
        {
            lock (_macrosLock)
            {
                return _macros.Values.OrderBy(m => m.Name).ToList();
            }
        }

        /// <summary>
        /// Kategoriye göre makroları listeler;
        /// </summary>
        public List<MacroDefinition> GetMacrosByCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return GetAllMacros();
            }

            lock (_macrosLock)
            {
                return _macros.Values;
                    .Where(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(m => m.Name)
                    .ToList();
            }
        }

        /// <summary>
        /// Makroyu adına göre getirir;
        /// </summary>
        public MacroDefinition GetMacro(string macroName)
        {
            ValidateMacroName(macroName);

            lock (_macrosLock)
            {
                if (_macros.TryGetValue(macroName, out var macro))
                {
                    return macro;
                }
            }

            throw new MacroEngineException($"Macro not found: {macroName}");
        }

        /// <summary>
        /// Makro araması yapar;
        /// </summary>
        public List<MacroDefinition> SearchMacros(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return GetAllMacros();
            }

            var term = searchTerm.ToLowerInvariant();

            lock (_macrosLock)
            {
                return _macros.Values;
                    .Where(m => m.Name.ToLowerInvariant().Contains(term) ||
                               (m.Description?.ToLowerInvariant()?.Contains(term) ?? false) ||
                               (m.Tags?.Any(t => t.ToLowerInvariant().Contains(term)) ?? false))
                    .OrderBy(m => m.Name)
                    .ToList();
            }
        }

        /// <summary>
        /// Makroyu dışa aktarır;
        /// </summary>
        public async Task<string> ExportMacroAsync(string macroName, ExportFormat format = ExportFormat.Json)
        {
            var macro = GetMacro(macroName);

            return format switch;
            {
                ExportFormat.Json => await _storage.ExportMacroJsonAsync(macro),
                ExportFormat.Xml => await _storage.ExportMacroXmlAsync(macro),
                _ => throw new MacroEngineException($"Unsupported export format: {format}")
            };
        }

        /// <summary>
        /// Makroyu içe aktarır;
        /// </summary>
        public async Task<MacroDefinition> ImportMacroAsync(string content, ImportFormat format)
        {
            MacroDefinition macro;

            try
            {
                macro = format switch;
                {
                    ImportFormat.Json => await _storage.ImportMacroJsonAsync(content),
                    ImportFormat.Xml => await _storage.ImportMacroXmlAsync(content),
                    _ => throw new MacroEngineException($"Unsupported import format: {format}")
                };
            }
            catch (Exception ex)
            {
                throw new MacroEngineException($"Failed to import macro: {ex.Message}", ex);
            }

            // Benzersiz isim oluştur;
            var originalName = macro.Name;
            var counter = 1;

            lock (_macrosLock)
            {
                while (_macros.ContainsKey(macro.Name))
                {
                    macro.Name = $"{originalName}_{counter++}";
                }

                macro.Id = Guid.NewGuid().ToString();
                macro.CreatedDate = DateTime.UtcNow;
                macro.ModifiedDate = DateTime.UtcNow;

                _macros[macro.Name] = macro;
            }

            await _storage.SaveMacroAsync(macro);
            _logger.LogInformation($"Imported macro: {originalName} as {macro.Name}");

            return macro;
        }

        /// <summary>
        /// Makroyu test eder (kuru çalıştırma)
        /// </summary>
        public async Task<MacroTestResult> TestMacroAsync(string macroName, Dictionary<string, object> parameters = null)
        {
            var macro = GetMacro(macroName);

            var result = new MacroTestResult;
            {
                MacroName = macroName,
                Steps = new List<StepTestResult>(),
                StartTime = DateTime.UtcNow;
            };

            foreach (var step in macro.Steps)
            {
                var stepResult = new StepTestResult;
                {
                    StepId = step.Id,
                    ActionType = step.ActionType,
                    IsValid = true,
                    ValidationErrors = new List<string>()
                };

                try
                {
                    // Adım doğrulama;
                    ValidateMacroStep(step);

                    // Komut doğrulama (eğer komut adımıysa)
                    if (step.ActionType == MacroActionType.Command && !string.IsNullOrEmpty(step.Command))
                    {
                        // Komut geçerlilik kontrolü;
                        if (!await _commandExecutor.ValidateCommandAsync(step.Command))
                        {
                            stepResult.IsValid = false;
                            stepResult.ValidationErrors.Add($"Invalid command: {step.Command}");
                        }
                    }

                    // Script doğrulama (eğer script adımıysa)
                    if (step.ActionType == MacroActionType.Script && !string.IsNullOrEmpty(step.ScriptContent))
                    {
                        try
                        {
                            await _scriptRunner.ValidateScriptAsync(step.ScriptContent, step.ScriptLanguage);
                        }
                        catch (Exception ex)
                        {
                            stepResult.IsValid = false;
                            stepResult.ValidationErrors.Add($"Script validation failed: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    stepResult.IsValid = false;
                    stepResult.ValidationErrors.Add(ex.Message);
                }

                result.Steps.Add(stepResult);
            }

            result.EndTime = DateTime.UtcNow;
            result.IsValid = result.Steps.All(s => s.IsValid);

            return result;
        }

        /// <summary>
        /// Makro istatistiklerini getirir;
        /// </summary>
        public MacroStatistics GetStatistics()
        {
            lock (_macrosLock)
            {
                var macros = _macros.Values.ToList();

                return new MacroStatistics;
                {
                    TotalMacros = macros.Count,
                    TotalSteps = macros.Sum(m => m.Steps?.Count ?? 0),
                    TotalExecutions = macros.Sum(m => m.ExecutionCount),
                    Categories = macros.GroupBy(m => m.Category)
                        .ToDictionary(g => g.Key, g => g.Count()),
                    MostExecuted = macros.OrderByDescending(m => m.ExecutionCount)
                        .Take(5)
                        .Select(m => new KeyValuePair<string, int>(m.Name, m.ExecutionCount))
                        .ToList(),
                    RecentlyCreated = macros.OrderByDescending(m => m.CreatedDate)
                        .Take(5)
                        .Select(m => m.Name)
                        .ToList()
                };
            }
        }

        #region Private Methods;

        private async Task LoadMacrosAsync()
        {
            var loadedMacros = await _storage.LoadAllMacrosAsync();

            lock (_macrosLock)
            {
                _macros.Clear();
                foreach (var macro in loadedMacros)
                {
                    if (!string.IsNullOrEmpty(macro.Name))
                    {
                        _macros[macro.Name] = macro;
                    }
                }
            }

            _logger.LogDebug($"Loaded {loadedMacros.Count} macros from storage.");
        }

        private async Task SaveMacroAsync(MacroDefinition macro)
        {
            lock (_macrosLock)
            {
                _macros[macro.Name] = macro;
            }

            await _storage.SaveMacroAsync(macro);
        }

        private void ValidateMacroName(string macroName)
        {
            if (string.IsNullOrWhiteSpace(macroName))
            {
                throw new ArgumentException("Macro name cannot be null or empty.", nameof(macroName));
            }

            if (macroName.Length > 100)
            {
                throw new ArgumentException("Macro name cannot exceed 100 characters.", nameof(macroName));
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(macroName, @"^[a-zA-Z0-9_\-\.\s]+$"))
            {
                throw new ArgumentException("Macro name can only contain letters, numbers, spaces, hyphens, underscores, and periods.", nameof(macroName));
            }
        }

        private void ValidateMacroSteps(List<MacroStep> steps)
        {
            if (steps == null || !steps.Any())
            {
                throw new ArgumentException("Macro must contain at least one step.", nameof(steps));
            }

            foreach (var step in steps)
            {
                ValidateMacroStep(step);
            }
        }

        private void ValidateMacroStep(MacroStep step)
        {
            if (step == null)
            {
                throw new ArgumentException("Macro step cannot be null.");
            }

            if (step.DelayMs < 0)
            {
                throw new ArgumentException("Delay cannot be negative.", nameof(step.DelayMs));
            }

            if (step.ActionType == MacroActionType.Command && string.IsNullOrWhiteSpace(step.Command))
            {
                throw new ArgumentException("Command type step must have a command.");
            }

            if (step.ActionType == MacroActionType.Script && string.IsNullOrWhiteSpace(step.ScriptContent))
            {
                throw new ArgumentException("Script type step must have script content.");
            }

            if (step.ActionType == MacroActionType.Wait && step.DelayMs <= 0)
            {
                throw new ArgumentException("Wait type step must have a positive delay.");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _globalCancellationTokenSource?.Dispose();
                    _executionLock?.Dispose();

                    if (_isRecording)
                    {
                        try
                        {
                            StopRecording(false).Wait(1000);
                        }
                        catch
                        {
                            // Ignore disposal errors;
                        }
                    }
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~MacroEngine()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Makro tanımı;
    /// </summary>
    public class MacroDefinition;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Category { get; set; } = "General";
        public string Version { get; set; } = "1.0";
        public List<MacroStep> Steps { get; set; } = new List<MacroStep>();
        public List<string> Tags { get; set; } = new List<string>();
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, object> ParametersSchema { get; set; } = new Dictionary<string, object>();
        public DateTime CreatedDate { get; set; }
        public DateTime ModifiedDate { get; set; }
        public DateTime? LastExecuted { get; set; }
        public int ExecutionCount { get; set; }
        public string Author { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int MaxExecutionTimeMs { get; set; } = 300000; // 5 minutes;
        public bool StopOnFailure { get; set; } = true;
    }

    /// <summary>
    /// Makro adımı;
    /// </summary>
    public class MacroStep;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public MacroActionType ActionType { get; set; }
        public string Command { get; set; }
        public string ScriptContent { get; set; }
        public string ScriptLanguage { get; set; } = "javascript";
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public int DelayMs { get; set; }
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
        public bool ContinueOnError { get; set; } = false;
        public int TimeoutMs { get; set; } = 30000; // 30 seconds;
        public string ExpectedResult { get; set; }
    }

    /// <summary>
    /// Makro eylem türleri;
    /// </summary>
    public enum MacroActionType;
    {
        Command,
        Script,
        Wait,
        Conditional,
        Loop,
        UserInput,
        SystemEvent;
    }

    /// <summary>
    /// Makro yürütme sonucu;
    /// </summary>
    public class MacroExecutionResult;
    {
        public string MacroName { get; set; }
        public bool IsSuccess { get; set; }
        public string Error { get; set; }
        public List<StepExecutionResult> StepResults { get; set; } = new List<StepExecutionResult>();
        public TimeSpan ExecutionTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public Dictionary<string, object> OutputData { get; set; } = new Dictionary<string, object>();
        public bool WasCancelled { get; set; }
        public bool StopOnFailure { get; set; } = true;
    }

    /// <summary>
    /// Adım yürütme sonucu;
    /// </summary>
    public class StepExecutionResult;
    {
        public string StepId { get; set; }
        public bool IsSuccess { get; set; }
        public string Error { get; set; }
        public TimeSpan ExecutionTime { get; set; }
        public object Result { get; set; }
        public Dictionary<string, object> Output { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Makro test sonucu;
    /// </summary>
    public class MacroTestResult;
    {
        public string MacroName { get; set; }
        public bool IsValid { get; set; }
        public List<StepTestResult> Steps { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public TimeSpan Duration => EndTime - StartTime;
    }

    /// <summary>
    /// Adım test sonucu;
    /// </summary>
    public class StepTestResult;
    {
        public string StepId { get; set; }
        public MacroActionType ActionType { get; set; }
        public bool IsValid { get; set; }
        public List<string> ValidationErrors { get; set; }
    }

    /// <summary>
    /// Makro istatistikleri;
    /// </summary>
    public class MacroStatistics;
    {
        public int TotalMacros { get; set; }
        public int TotalSteps { get; set; }
        public int TotalExecutions { get; set; }
        public Dictionary<string, int> Categories { get; set; } = new Dictionary<string, int>();
        public List<KeyValuePair<string, int>> MostExecuted { get; set; } = new List<KeyValuePair<string, int>>();
        public List<string> RecentlyCreated { get; set; } = new List<string>();
    }

    /// <summary>
    /// İçe/dışa aktarma formatları;
    /// </summary>
    public enum ExportFormat;
    {
        Json,
        Xml;
    }

    public enum ImportFormat;
    {
        Json,
        Xml;
    }

    #endregion;

    #region Internal Components;

    /// <summary>
    /// Makro kaydedici;
    /// </summary>
    internal class MacroRecorder;
    {
        private readonly ILogger _logger;
        private List<MacroStep> _recordedSteps;
        private DateTime _recordingStartTime;
        private bool _isRecording;

        public MacroRecorder(ILogger logger)
        {
            _logger = logger;
        }

        public void StartRecording(string macroName)
        {
            if (_isRecording)
            {
                throw new InvalidOperationException("Already recording");
            }

            _recordedSteps = new List<MacroStep>();
            _recordingStartTime = DateTime.UtcNow;
            _isRecording = true;

            _logger.LogDebug($"Started recording macro: {macroName}");
        }

        public List<MacroStep> StopRecording()
        {
            if (!_isRecording)
            {
                throw new InvalidOperationException("Not recording");
            }

            var steps = _recordedSteps;
            var duration = DateTime.UtcNow - _recordingStartTime;

            _recordedSteps = null;
            _isRecording = false;

            _logger.LogDebug($"Stopped recording. Duration: {duration.TotalSeconds:F2}s, Steps: {steps.Count}");

            return steps;
        }

        public void RecordStep(MacroStep step)
        {
            if (!_isRecording)
            {
                throw new InvalidOperationException("Not recording");
            }

            _recordedSteps.Add(step);
        }

        public bool IsRecording => _isRecording;
    }

    /// <summary>
    /// Makro oynatıcı;
    /// </summary>
    internal class MacroPlayer;
    {
        private readonly ILogger _logger;
        private readonly IScriptRunner _scriptRunner;
        private readonly ICommandExecutor _commandExecutor;

        public MacroPlayer(
            ILogger logger,
            IScriptRunner scriptRunner,
            ICommandExecutor commandExecutor)
        {
            _logger = logger;
            _scriptRunner = scriptRunner;
            _commandExecutor = commandExecutor;
        }

        public async Task<MacroExecutionResult> ExecuteMacroAsync(
            MacroDefinition macro,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            var result = new MacroExecutionResult;
            {
                MacroName = macro.Name,
                StartTime = DateTime.UtcNow,
                StopOnFailure = macro.StopOnFailure;
            };

            try
            {
                _logger.LogInformation($"Executing macro: {macro.Name} with {macro.Steps.Count} steps");

                foreach (var step in macro.Steps)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.WasCancelled = true;
                        _logger.LogWarning($"Macro execution cancelled: {macro.Name}");
                        break;
                    }

                    var stepResult = await ExecuteStepAsync(step, parameters, cancellationToken);
                    result.StepResults.Add(stepResult);

                    if (!stepResult.IsSuccess && macro.StopOnFailure)
                    {
                        result.Error = $"Step failed: {stepResult.Error}";
                        _logger.LogError($"Macro execution stopped due to step failure: {macro.Name}");
                        break;
                    }

                    // Adımlar arası gecikme;
                    if (step.DelayMs > 0)
                    {
                        await Task.Delay(step.DelayMs, cancellationToken);
                    }
                }

                result.IsSuccess = result.StepResults.All(s => s.IsSuccess) && !result.WasCancelled;
            }
            catch (OperationCanceledException)
            {
                result.WasCancelled = true;
                result.Error = "Execution cancelled";
                _logger.LogWarning($"Macro execution cancelled: {macro.Name}");
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Error = ex.Message;
                _logger.LogError(ex, $"Failed to execute macro: {macro.Name}");
            }
            finally
            {
                result.EndTime = DateTime.UtcNow;
                result.ExecutionTime = result.EndTime - result.StartTime;
                _logger.LogInformation($"Finished executing macro: {macro.Name} in {result.ExecutionTime.TotalSeconds:F2}s");
            }

            return result;
        }

        private async Task<StepExecutionResult> ExecuteStepAsync(
            MacroStep step,
            Dictionary<string, object> parameters,
            CancellationToken cancellationToken)
        {
            var result = new StepExecutionResult;
            {
                StepId = step.Id;
            };

            var stepStartTime = DateTime.UtcNow;

            try
            {
                _logger.LogDebug($"Executing macro step: {step.ActionType}");

                object stepResult = null;

                switch (step.ActionType)
                {
                    case MacroActionType.Command:
                        stepResult = await ExecuteCommandStepAsync(step, cancellationToken);
                        break;

                    case MacroActionType.Script:
                        stepResult = await ExecuteScriptStepAsync(step, cancellationToken);
                        break;

                    case MacroActionType.Wait:
                        await Task.Delay(step.DelayMs > 0 ? step.DelayMs : 1000, cancellationToken);
                        stepResult = $"Waited {step.DelayMs}ms";
                        break;

                    default:
                        throw new NotSupportedException($"Action type not supported: {step.ActionType}");
                }

                result.IsSuccess = true;
                result.Result = stepResult;
                result.Output["Result"] = stepResult;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.Error = ex.Message;
                _logger.LogError(ex, $"Failed to execute macro step: {step.ActionType}");
            }
            finally
            {
                result.ExecutionTime = DateTime.UtcNow - stepStartTime;
            }

            return result;
        }

        private async Task<object> ExecuteCommandStepAsync(MacroStep step, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(step.Command))
            {
                throw new ArgumentException("Command cannot be null or empty");
            }

            var commandResult = await _commandExecutor.ExecuteCommandAsync(
                step.Command,
                step.Parameters,
                cancellationToken);

            return commandResult;
        }

        private async Task<object> ExecuteScriptStepAsync(MacroStep step, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(step.ScriptContent))
            {
                throw new ArgumentException("Script content cannot be null or empty");
            }

            var scriptResult = await _scriptRunner.ExecuteScriptAsync(
                step.ScriptContent,
                step.ScriptLanguage,
                step.Parameters,
                cancellationToken);

            return scriptResult;
        }
    }

    /// <summary>
    /// Makro depolama;
    /// </summary>
    internal class MacroStorage;
    {
        private readonly ILogger _logger;
        private string _storagePath;
        private readonly JsonSerializerOptions _jsonOptions;

        public MacroStorage(ILogger logger)
        {
            _logger = logger;
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new DictionaryStringObjectJsonConverter() }
            };
        }

        public async Task InitializeAsync(string storagePath = null)
        {
            _storagePath = storagePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "Macros");

            Directory.CreateDirectory(_storagePath);
            _logger.LogDebug($"Macro storage initialized at: {_storagePath}");
        }

        public async Task SaveMacroAsync(MacroDefinition macro)
        {
            var filePath = GetMacroFilePath(macro.Name);
            var json = JsonSerializer.Serialize(macro, _jsonOptions);

            await File.WriteAllTextAsync(filePath, json);

            _logger.LogDebug($"Saved macro to file: {filePath}");
        }

        public async Task<MacroDefinition> LoadMacroAsync(string macroName)
        {
            var filePath = GetMacroFilePath(macroName);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Macro file not found: {filePath}");
            }

            var json = await File.ReadAllTextAsync(filePath);
            var macro = JsonSerializer.Deserialize<MacroDefinition>(json, _jsonOptions);

            _logger.LogDebug($"Loaded macro from file: {filePath}");

            return macro;
        }

        public async Task<List<MacroDefinition>> LoadAllMacrosAsync()
        {
            var macros = new List<MacroDefinition>();

            if (!Directory.Exists(_storagePath))
            {
                return macros;
            }

            var files = Directory.GetFiles(_storagePath, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var macro = JsonSerializer.Deserialize<MacroDefinition>(json, _jsonOptions);

                    if (macro != null)
                    {
                        macros.Add(macro);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to load macro from file: {file}");
                }
            }

            return macros;
        }

        public async Task DeleteMacroAsync(string macroName)
        {
            var filePath = GetMacroFilePath(macroName);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogDebug($"Deleted macro file: {filePath}");
            }
        }

        public async Task<string> ExportMacroJsonAsync(MacroDefinition macro)
        {
            return JsonSerializer.Serialize(macro, _jsonOptions);
        }

        public async Task<string> ExportMacroXmlAsync(MacroDefinition macro)
        {
            // Basit XML serialization;
            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            xml.AppendLine($"<Macro name=\"{macro.Name}\">");
            xml.AppendLine($"  <Description>{macro.Description}</Description>");
            xml.AppendLine($"  <Category>{macro.Category}</Category>");
            xml.AppendLine("  <Steps>");

            foreach (var step in macro.Steps)
            {
                xml.AppendLine($"    <Step type=\"{step.ActionType}\">");
                xml.AppendLine($"      <Command>{step.Command}</Command>");
                xml.AppendLine($"      <DelayMs>{step.DelayMs}</DelayMs>");
                xml.AppendLine("    </Step>");
            }

            xml.AppendLine("  </Steps>");
            xml.AppendLine("</Macro>");

            return xml.ToString();
        }

        public async Task<MacroDefinition> ImportMacroJsonAsync(string json)
        {
            return JsonSerializer.Deserialize<MacroDefinition>(json, _jsonOptions);
        }

        public async Task<MacroDefinition> ImportMacroXmlAsync(string xml)
        {
            // Basit XML parsing;
            // Not: Gerçek uygulamada XML parser kullanılmalı;
            var macro = new MacroDefinition;
            {
                Id = Guid.NewGuid().ToString(),
                CreatedDate = DateTime.UtcNow,
                ModifiedDate = DateTime.UtcNow;
            };

            // XML parsing logic here;
            // Bu örnek için basit parsing;

            return macro;
        }

        private string GetMacroFilePath(string macroName)
        {
            var safeFileName = GetSafeFileName(macroName);
            return Path.Combine(_storagePath, $"{safeFileName}.json");
        }

        private string GetSafeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Concat(fileName.Where(c => !invalidChars.Contains(c)));
        }
    }

    /// <summary>
    /// JSON converter for Dictionary<string, object>
    /// </summary>
    internal class DictionaryStringObjectJsonConverter : System.Text.Json.Serialization.JsonConverter<Dictionary<string, object>>
    {
        public override Dictionary<string, object> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException();
            }

            var dictionary = new Dictionary<string, object>();

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return dictionary;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException();
                }

                string propertyName = reader.GetString();

                reader.Read();

                dictionary[propertyName] = ExtractValue(ref reader, options);
            }

            throw new JsonException();
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<string, object> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                WriteValue(writer, kvp.Value, options);
            }

            writer.WriteEndObject();
        }

        private object ExtractValue(ref Utf8JsonReader reader, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    return reader.GetString();
                case JsonTokenType.Number:
                    if (reader.TryGetInt32(out int intValue))
                        return intValue;
                    if (reader.TryGetInt64(out long longValue))
                        return longValue;
                    return reader.GetDecimal();
                case JsonTokenType.True:
                    return true;
                case JsonTokenType.False:
                    return false;
                case JsonTokenType.Null:
                    return null;
                case JsonTokenType.StartObject:
                    return Read(ref reader, typeof(Dictionary<string, object>), options);
                case JsonTokenType.StartArray:
                    var list = new List<object>();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        list.Add(ExtractValue(ref reader, options));
                    }
                    return list;
                default:
                    throw new JsonException($"Unsupported token type: {reader.TokenType}");
            }
        }

        private void WriteValue(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
        {
            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    break;
                case string s:
                    writer.WriteStringValue(s);
                    break;
                case bool b:
                    writer.WriteBooleanValue(b);
                    break;
                case int i:
                    writer.WriteNumberValue(i);
                    break;
                case long l:
                    writer.WriteNumberValue(l);
                    break;
                case decimal d:
                    writer.WriteNumberValue(d);
                    break;
                case double dbl:
                    writer.WriteNumberValue(dbl);
                    break;
                case Dictionary<string, object> dict:
                    Write(writer, dict, options);
                    break;
                case IEnumerable<object> list:
                    writer.WriteStartArray();
                    foreach (var item in list)
                    {
                        WriteValue(writer, item, options);
                    }
                    writer.WriteEndArray();
                    break;
                default:
                    JsonSerializer.Serialize(writer, value, value.GetType(), options);
                    break;
            }
        }
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Makro motoru istisnası;
    /// </summary>
    public class MacroEngineException : Exception
    {
        public string MacroName { get; }
        public DateTime Timestamp { get; }

        public MacroEngineException(string message) : base(message)
        {
            Timestamp = DateTime.UtcNow;
        }

        public MacroEngineException(string message, Exception innerException)
            : base(message, innerException)
        {
            Timestamp = DateTime.UtcNow;
        }

        public MacroEngineException(string macroName, string message)
            : base(message)
        {
            MacroName = macroName;
            Timestamp = DateTime.UtcNow;
        }
    }

    #endregion;
}
