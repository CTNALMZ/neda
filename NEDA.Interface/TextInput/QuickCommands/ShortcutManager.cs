using NEDA.Core.Common;
using NEDA.Core.Configuration.AppSettings;
using NEDA.Core.Logging;
using NEDA.GameDesign.GameplayDesign.Playtesting;
using NEDA.Interface.TextInput.MacroSupport;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using static NEDA.CharacterSystems.DialogueSystem.SubtitleManagement.SubtitleEngine;

namespace NEDA.Interface.TextInput.QuickCommands;
{
    /// <summary>
    /// Klavye kısayol yöneticisi - Kullanıcı tanımlı kısayolları yönetir ve işler;
    /// </summary>
    public class ShortcutManager : IShortcutManager, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IConfigurationManager _configManager;
        private readonly MacroEngine _macroEngine;

        private readonly ConcurrentDictionary<string, ShortcutDefinition> _shortcuts;
        private readonly ConcurrentDictionary<KeyCombination, string> _keyToShortcutId;
        private readonly ConcurrentDictionary<string, List<ShortcutBinding>> _categoryBindings;
        private readonly object _registrationLock = new object();

        private bool _isInitialized;
        private bool _isListening;
        private CancellationTokenSource _listeningCts;
        private ShortcutConfiguration _configuration;
        private ShortcutStatistics _statistics;

        /// <summary>
        /// Kısayol yöneticisi durumu;
        /// </summary>
        public ShortcutManagerState State { get; private set; }

        /// <summary>
        /// Kısayol olayları;
        /// </summary>
        public event EventHandler<ShortcutExecutedEventArgs> ShortcutExecuted;
        public event EventHandler<ShortcutRegisteredEventArgs> ShortcutRegistered;
        public event EventHandler<ShortcutConflictEventArgs> ShortcutConflictDetected;

        /// <summary>
        /// Aktif kısayol bağlamı;
        /// </summary>
        public ShortcutContext CurrentContext { get; private set; }

        /// <summary>
        /// Yeni bir ShortcutManager örneği oluşturur;
        /// </summary>
        public ShortcutManager(ILogger logger, IConfigurationManager configManager, MacroEngine macroEngine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _macroEngine = macroEngine ?? throw new ArgumentNullException(nameof(macroEngine));

            _shortcuts = new ConcurrentDictionary<string, ShortcutDefinition>();
            _keyToShortcutId = new ConcurrentDictionary<KeyCombination, string>();
            _categoryBindings = new ConcurrentDictionary<string, List<ShortcutBinding>>();

            _configuration = new ShortcutConfiguration();
            _statistics = new ShortcutStatistics();
            State = ShortcutManagerState.Stopped;
            CurrentContext = ShortcutContext.Global;

            _listeningCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Kısayol yöneticisini başlatır;
        /// </summary>
        public async Task InitializeAsync(ShortcutConfiguration configuration = null)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Kısayol yöneticisi başlatılıyor...");

                // Yapılandırmayı güncelle;
                if (configuration != null)
                    _configuration = configuration;
                else;
                    await LoadConfigurationAsync();

                State = ShortcutManagerState.Initializing;

                // Kısayolları yükle;
                await LoadShortcutsAsync();

                // Makroları başlat;
                await InitializeMacrosAsync();

                // Klavye dinleyicisini başlat;
                await StartKeyboardListenerAsync();

                // İstatistikleri sıfırla;
                ResetStatistics();

                _isInitialized = true;
                State = ShortcutManagerState.Ready;

                _logger.LogInformation($"Kısayol yöneticisi başlatıldı: {_shortcuts.Count} kısayol yüklendi");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol yöneticisi başlatma hatası: {ex.Message}", ex);
                State = ShortcutManagerState.Error;
                throw new ShortcutManagerException("Kısayol yöneticisi başlatılamadı", ex);
            }
        }

        /// <summary>
        /// Yeni bir kısayol kaydeder;
        /// </summary>
        public ShortcutRegistrationResult RegisterShortcut(ShortcutDefinition shortcut)
        {
            ValidateManagerState();

            try
            {
                if (shortcut == null)
                    throw new ArgumentNullException(nameof(shortcut));

                if (string.IsNullOrWhiteSpace(shortcut.Id))
                    throw new ArgumentException("Kısayol ID'si boş olamaz", nameof(shortcut.Id));

                if (shortcut.KeyCombination == null)
                    throw new ArgumentException("Tuş kombinasyonu belirtilmelidir", nameof(shortcut.KeyCombination));

                lock (_registrationLock)
                {
                    // Çakışma kontrolü;
                    var conflictResult = CheckShortcutConflict(shortcut);
                    if (conflictResult.HasConflict && _configuration.ConflictResolution == ConflictResolution.Strict)
                    {
                        OnShortcutConflictDetected(conflictResult);
                        return new ShortcutRegistrationResult;
                        {
                            Success = false,
                            ShortcutId = shortcut.Id,
                            Error = $"Kısayol çakışması: {conflictResult.ConflictingShortcutId}",
                            ConflictResult = conflictResult;
                        };
                    }

                    // Kısayolu kaydet;
                    bool added = _shortcuts.TryAdd(shortcut.Id, shortcut);
                    if (!added)
                    {
                        return new ShortcutRegistrationResult;
                        {
                            Success = false,
                            ShortcutId = shortcut.Id,
                            Error = "Kısayol ID'si zaten mevcut"
                        };
                    }

                    // Tuş kombinasyonunu eşle;
                    _keyToShortcutId[shortcut.KeyCombination] = shortcut.Id;

                    // Kategori bağlantısını ekle;
                    if (!string.IsNullOrWhiteSpace(shortcut.Category))
                    {
                        var bindings = _categoryBindings.GetOrAdd(shortcut.Category, new List<ShortcutBinding>());
                        bindings.Add(new ShortcutBinding;
                        {
                            ShortcutId = shortcut.Id,
                            KeyCombination = shortcut.KeyCombination,
                            Context = shortcut.Context;
                        });
                    }

                    _logger.LogInformation($"Kısayol kaydedildi: {shortcut.Id} - {shortcut.KeyCombination}");

                    // Olay tetikle;
                    OnShortcutRegistered(new ShortcutRegisteredEventArgs;
                    {
                        ShortcutId = shortcut.Id,
                        KeyCombination = shortcut.KeyCombination,
                        Category = shortcut.Category,
                        Timestamp = DateTime.UtcNow;
                    });

                    // Kısayolu kaydet;
                    SaveShortcutToStorage(shortcut);

                    return new ShortcutRegistrationResult;
                    {
                        Success = true,
                        ShortcutId = shortcut.Id,
                        Message = "Kısayol başarıyla kaydedildi"
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol kaydetme hatası: {ex.Message}", ex);
                throw new ShortcutRegistrationException($"Kısayol kaydedilemedi: {shortcut?.Id}", ex);
            }
        }

        /// <summary>
        /// Kısayol bağlamını değiştirir;
        /// </summary>
        public void SwitchContext(ShortcutContext newContext)
        {
            ValidateManagerState();

            try
            {
                var oldContext = CurrentContext;
                CurrentContext = newContext;

                _logger.LogDebug($"Kısayol bağlamı değiştirildi: {oldContext} -> {newContext}");

                // Bağlam değişikliğini aktif kısayollara yay;
                UpdateShortcutsForContext(newContext);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Bağlam değiştirme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Kısayolu yürütür;
        /// </summary>
        public async Task<ShortcutExecutionResult> ExecuteShortcutAsync(
            string shortcutId,
            ExecutionParameters parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateManagerState();

            try
            {
                if (string.IsNullOrWhiteSpace(shortcutId))
                    throw new ArgumentException("Kısayol ID'si belirtilmelidir", nameof(shortcutId));

                var startTime = DateTime.UtcNow;

                // Kısayolu bul;
                if (!_shortcuts.TryGetValue(shortcutId, out var shortcut))
                {
                    return new ShortcutExecutionResult;
                    {
                        Success = false,
                        ShortcutId = shortcutId,
                        Error = "Kısayol bulunamadı"
                    };
                }

                // Bağlam kontrolü;
                if (!IsShortcutAvailableInCurrentContext(shortcut))
                {
                    return new ShortcutExecutionResult;
                    {
                        Success = false,
                        ShortcutId = shortcutId,
                        Error = $"Kısayol mevcut bağlamda kullanılamaz: {CurrentContext}"
                    };
                }

                // Kısayol tipine göre işlem;
                ShortcutExecutionResult result;
                switch (shortcut.Type)
                {
                    case ShortcutType.Command:
                        result = await ExecuteCommandShortcutAsync(shortcut, parameters, cancellationToken);
                        break;
                    case ShortcutType.Macro:
                        result = await ExecuteMacroShortcutAsync(shortcut, parameters, cancellationToken);
                        break;
                    case ShortcutType.Script:
                        result = await ExecuteScriptShortcutAsync(shortcut, parameters, cancellationToken);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen kısayol tipi: {shortcut.Type}");
                }

                // İstatistikleri güncelle;
                UpdateExecutionStatistics(shortcut, result, DateTime.UtcNow - startTime);

                // Olay tetikle;
                OnShortcutExecuted(new ShortcutExecutedEventArgs;
                {
                    ShortcutId = shortcutId,
                    Success = result.Success,
                    ExecutionTime = result.ExecutionTime,
                    Parameters = parameters,
                    Timestamp = DateTime.UtcNow;
                });

                return result;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Kısayol yürütme iptal edildi: {shortcutId}");
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol yürütme hatası: {ex.Message}", ex);
                throw new ShortcutExecutionException($"Kısayol yürütülemedi: {shortcutId}", ex);
            }
        }

        /// <summary>
        /// Tuş kombinasyonunu kısayola çevirir ve yürütür;
        /// </summary>
        public async Task<ShortcutExecutionResult> ProcessKeyCombinationAsync(
            KeyCombination keyCombination,
            ExecutionParameters parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateManagerState();

            try
            {
                // Kısayol ID'sini bul;
                if (!_keyToShortcutId.TryGetValue(keyCombination, out var shortcutId))
                {
                    return new ShortcutExecutionResult;
                    {
                        Success = false,
                        KeyCombination = keyCombination,
                        Error = "Tuş kombinasyonu için tanımlı kısayol bulunamadı"
                    };
                }

                // Kısayolu yürüt;
                return await ExecuteShortcutAsync(shortcutId, parameters, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Tuş kombinasyonu işleme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Kısayol arama yapar;
        /// </summary>
        public IEnumerable<ShortcutDefinition> SearchShortcuts(
            ShortcutSearchCriteria criteria,
            ShortcutSearchOptions options = null)
        {
            ValidateManagerState();

            try
            {
                var searchOptions = options ?? new ShortcutSearchOptions();
                var query = _shortcuts.Values.AsEnumerable();

                // Arama kriterlerini uygula;
                if (!string.IsNullOrWhiteSpace(criteria.Name))
                {
                    query = query.Where(s => s.Name.Contains(criteria.Name,
                        searchOptions.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(criteria.Category))
                {
                    query = query.Where(s => s.Category != null &&
                        s.Category.Contains(criteria.Category,
                        searchOptions.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
                }

                if (criteria.Context.HasValue)
                {
                    query = query.Where(s => s.Context == criteria.Context.Value);
                }

                if (criteria.Type.HasValue)
                {
                    query = query.Where(s => s.Type == criteria.Type.Value);
                }

                // Sıralama;
                if (!string.IsNullOrWhiteSpace(searchOptions.SortBy))
                {
                    query = ApplySorting(query, searchOptions.SortBy, searchOptions.SortDescending);
                }

                // Sayfalama;
                if (searchOptions.PageSize > 0)
                {
                    query = query;
                        .Skip((searchOptions.PageNumber - 1) * searchOptions.PageSize)
                        .Take(searchOptions.PageSize);
                }

                return query.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol arama hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Kısayol gruplarını alır;
        /// </summary>
        public IEnumerable<ShortcutGroup> GetShortcutGroups()
        {
            ValidateManagerState();

            try
            {
                var groups = _shortcuts.Values;
                    .GroupBy(s => s.Category ?? "Diğer")
                    .Select(g => new ShortcutGroup;
                    {
                        Category = g.Key,
                        Shortcuts = g.ToList(),
                        Count = g.Count(),
                        IsActive = g.Any(s => IsShortcutAvailableInCurrentContext(s))
                    })
                    .OrderBy(g => g.Category)
                    .ToList();

                return groups;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol grupları alma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Kısayol önerileri üretir;
        /// </summary>
        public async Task<IEnumerable<ShortcutSuggestion>> GenerateSuggestionsAsync(
            UsagePattern pattern,
            SuggestionOptions options = null)
        {
            ValidateManagerState();

            try
            {
                var suggestionOptions = options ?? new SuggestionOptions();
                var suggestions = new List<ShortcutSuggestion>();

                // Kullanım sıklığına göre öneri;
                var frequentShortcuts = GetFrequentlyUsedShortcuts(pattern);
                suggestions.AddRange(frequentShortcuts);

                // Benzer kısayollar;
                var similarShortcuts = await FindSimilarShortcutsAsync(pattern);
                suggestions.AddRange(similarShortcuts);

                // Optimizasyon önerileri;
                var optimizationSuggestions = GenerateOptimizationSuggestions();
                suggestions.AddRange(optimizationSuggestions);

                // Önerileri sırala ve filtrele;
                return suggestions;
                    .Where(s => s.ConfidenceScore >= suggestionOptions.MinConfidence)
                    .OrderByDescending(s => s.ConfidenceScore)
                    .Take(suggestionOptions.MaxSuggestions);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol önerisi oluşturma hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Kısayol yapılandırmasını kaydeder;
        /// </summary>
        public async Task SaveConfigurationAsync()
        {
            ValidateManagerState();

            try
            {
                _logger.LogInformation("Kısayol yapılandırması kaydediliyor...");

                // Tüm kısayolları al;
                var allShortcuts = _shortcuts.Values.ToList();

                // Yapılandırma nesnesini oluştur;
                var config = new ShortcutManagerConfig;
                {
                    Shortcuts = allShortcuts,
                    Configuration = _configuration,
                    Statistics = _statistics,
                    LastSaved = DateTime.UtcNow,
                    Version = "1.0.0"
                };

                // JSON olarak kaydet;
                var configJson = SerializeConfiguration(config);
                await _configManager.SaveSettingAsync("ShortcutManager/Config", configJson);

                // Yedekle;
                await CreateBackupAsync(config);

                _logger.LogInformation($"Kısayol yapılandırması kaydedildi: {allShortcuts.Count} kısayol");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol yapılandırması kaydetme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Kısayol yöneticisini durdurur;
        /// </summary>
        public async Task ShutdownAsync()
        {
            if (!_isInitialized)
                return;

            try
            {
                _logger.LogInformation("Kısayol yöneticisi durduruluyor...");

                State = ShortcutManagerState.Stopping;

                // Klavye dinleyicisini durdur;
                await StopKeyboardListenerAsync();

                // Aktif işlemleri tamamla;
                await CompletePendingOperationsAsync();

                // Yapılandırmayı kaydet;
                await SaveConfigurationAsync();

                // Kaynakları serbest bırak;
                await ReleaseResourcesAsync();

                _isInitialized = false;
                State = ShortcutManagerState.Stopped;

                _logger.LogInformation("Kısayol yöneticisi başarıyla durduruldu");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol yöneticisi durdurma hatası: {ex.Message}", ex);
                throw;
            }
        }

        #region Private Methods;

        private void ValidateManagerState()
        {
            if (!_isInitialized || State != ShortcutManagerState.Ready)
                throw new InvalidOperationException("Kısayol yöneticisi hazır durumda değil");
        }

        private bool IsShortcutAvailableInCurrentContext(ShortcutDefinition shortcut)
        {
            return shortcut.Context == ShortcutContext.Global ||
                   shortcut.Context == CurrentContext;
        }

        private async Task<ShortcutExecutionResult> ExecuteCommandShortcutAsync(
            ShortcutDefinition shortcut,
            ExecutionParameters parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // Komutu oluştur;
                var command = BuildCommandFromShortcut(shortcut, parameters);

                // Komutu yürüt;
                var result = await command.ExecuteAsync(cancellationToken);

                return new ShortcutExecutionResult;
                {
                    Success = result.Success,
                    ShortcutId = shortcut.Id,
                    CommandResult = result,
                    ExecutionTime = result.ExecutionTime,
                    Message = result.Message;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Komut kısayolu yürütme hatası: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<ShortcutExecutionResult> ExecuteMacroShortcutAsync(
            ShortcutDefinition shortcut,
            ExecutionParameters parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // Makroyu al;
                var macro = await _macroEngine.GetMacroAsync(shortcut.ActionReference);

                // Parametreleri ayarla;
                if (parameters != null)
                {
                    macro.Parameters = parameters;
                }

                // Makroyu çalıştır;
                var result = await _macroEngine.ExecuteMacroAsync(macro, cancellationToken);

                return new ShortcutExecutionResult;
                {
                    Success = result.Success,
                    ShortcutId = shortcut.Id,
                    MacroResult = result,
                    ExecutionTime = result.ExecutionTime,
                    Message = result.Message;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Makro kısayolu yürütme hatası: {ex.Message}", ex);
                throw;
            }
        }

        private async Task<ShortcutExecutionResult> ExecuteScriptShortcutAsync(
            ShortcutDefinition shortcut,
            ExecutionParameters parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                // Script motorunu al;
                var scriptEngine = GetScriptEngine(shortcut.ScriptType);

                // Script'i çalıştır;
                var result = await scriptEngine.ExecuteScriptAsync(
                    shortcut.ActionReference,
                    parameters,
                    cancellationToken);

                return new ShortcutExecutionResult;
                {
                    Success = result.Success,
                    ShortcutId = shortcut.Id,
                    ScriptResult = result,
                    ExecutionTime = result.ExecutionTime,
                    Message = result.Message;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Script kısayolu yürütme hatası: {ex.Message}", ex);
                throw;
            }
        }

        private async Task LoadShortcutsAsync()
        {
            try
            {
                _logger.LogDebug("Kısayollar yükleniyor...");

                // Yapılandırmadan yükle;
                var configJson = await _configManager.GetSettingAsync<string>("ShortcutManager/Config");
                if (!string.IsNullOrWhiteSpace(configJson))
                {
                    var config = DeserializeConfiguration(configJson);
                    foreach (var shortcut in config.Shortcuts)
                    {
                        _shortcuts.TryAdd(shortcut.Id, shortcut);
                        _keyToShortcutId[shortcut.KeyCombination] = shortcut.Id;
                    }
                }

                // Varsayılan kısayolları ekle;
                await LoadDefaultShortcutsAsync();

                _logger.LogInformation($"{_shortcuts.Count} kısayol yüklendi");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Kısayol yükleme hatası: {ex.Message}", ex);
                throw;
            }
        }

        private async Task LoadDefaultShortcutsAsync()
        {
            var defaultShortcuts = new List<ShortcutDefinition>
            {
                new ShortcutDefinition;
                {
                    Id = "save_project",
                    Name = "Proje Kaydet",
                    KeyCombination = new KeyCombination(Key.S, ModifierKeys.Control),
                    Type = ShortcutType.Command,
                    ActionReference = "Project.Save",
                    Category = "File",
                    Context = ShortcutContext.Project,
                    Description = "Aktif projeyi kaydeder"
                },
                new ShortcutDefinition;
                {
                    Id = "undo_action",
                    Name = "Geri Al",
                    KeyCombination = new KeyCombination(Key.Z, ModifierKeys.Control),
                    Type = ShortcutType.Command,
                    ActionReference = "Edit.Undo",
                    Category = "Edit",
                    Context = ShortcutContext.Global,
                    Description = "Son işlemi geri alır"
                },
                new ShortcutDefinition;
                {
                    Id = "find_text",
                    Name = "Bul",
                    KeyCombination = new KeyCombination(Key.F, ModifierKeys.Control),
                    Type = ShortcutType.Command,
                    ActionReference = "Edit.Find",
                    Category = "Edit",
                    Context = ShortcutContext.Editor,
                    Description = "Metin arama penceresini açar"
                }
            };

            foreach (var shortcut in defaultShortcuts)
            {
                if (!_shortcuts.ContainsKey(shortcut.Id))
                {
                    _shortcuts.TryAdd(shortcut.Id, shortcut);
                    _keyToShortcutId[shortcut.KeyCombination] = shortcut.Id;
                }
            }
        }

        private async Task StartKeyboardListenerAsync()
        {
            if (_isListening)
                return;

            try
            {
                _isListening = true;
                _listeningCts = new CancellationTokenSource();

                // Klavye dinleyici thread'ini başlat;
                _ = Task.Run(async () => await KeyboardListenerLoopAsync(_listeningCts.Token));

                _logger.LogInformation("Klavye dinleyicisi başlatıldı");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Klavye dinleyicisi başlatma hatası: {ex.Message}", ex);
                _isListening = false;
                throw;
            }
        }

        private async Task KeyboardListenerLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _isListening)
                {
                    // Klavye girdisini dinle (platforma özel implementasyon)
                    var keyEvent = await WaitForKeyEventAsync(cancellationToken);

                    if (keyEvent != null && keyEvent.IsPressed)
                    {
                        // Tuş kombinasyonunu oluştur;
                        var keyCombination = new KeyCombination(
                            keyEvent.Key,
                            keyEvent.Modifiers);

                        // Kısayol kontrolü;
                        if (_keyToShortcutId.ContainsKey(keyCombination))
                        {
                            // Kısayolu yürüt;
                            await ProcessKeyCombinationAsync(keyCombination);
                        }
                    }

                    await Task.Delay(10, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal durdurma;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Klavye dinleyici döngü hatası: {ex.Message}", ex);
            }
        }

        private void ResetStatistics()
        {
            _statistics = new ShortcutStatistics;
            {
                TotalExecutions = 0,
                TotalSuccess = 0,
                TotalErrors = 0,
                MostUsedShortcut = null,
                LastExecution = null,
                StartTime = DateTime.UtcNow;
            };
        }

        private void UpdateExecutionStatistics(ShortcutDefinition shortcut, ShortcutExecutionResult result, TimeSpan executionTime)
        {
            lock (_statistics)
            {
                _statistics.TotalExecutions++;
                if (result.Success)
                    _statistics.TotalSuccess++;
                else;
                    _statistics.TotalErrors++;

                _statistics.LastExecution = new ExecutionRecord;
                {
                    ShortcutId = shortcut.Id,
                    Success = result.Success,
                    ExecutionTime = executionTime,
                    Timestamp = DateTime.UtcNow;
                };

                // En çok kullanılan kısayol güncellemesi;
                if (!_statistics.UsageCounts.ContainsKey(shortcut.Id))
                    _statistics.UsageCounts[shortcut.Id] = 0;

                _statistics.UsageCounts[shortcut.Id]++;

                var mostUsed = _statistics.UsageCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
                _statistics.MostUsedShortcut = mostUsed.Key;
            }
        }

        private ShortcutConflictResult CheckShortcutConflict(ShortcutDefinition shortcut)
        {
            var result = new ShortcutConflictResult;
            {
                HasConflict = false,
                NewShortcut = shortcut;
            };

            // Tuş kombinasyonu çakışması;
            if (_keyToShortcutId.ContainsKey(shortcut.KeyCombination))
            {
                var existingId = _keyToShortcutId[shortcut.KeyCombination];
                result.HasConflict = true;
                result.ConflictType = ConflictType.KeyCombination;
                result.ConflictingShortcutId = existingId;
                result.ExistingShortcut = _shortcuts[existingId];
            }

            // ID çakışması;
            if (_shortcuts.ContainsKey(shortcut.Id))
            {
                result.HasConflict = true;
                result.ConflictType = ConflictType.Id;
                result.ConflictingShortcutId = shortcut.Id;
                result.ExistingShortcut = _shortcuts[shortcut.Id];
            }

            return result;
        }

        private async Task CompletePendingOperationsAsync()
        {
            // Bekleyen kısayol yürütmelerini tamamla;
            await Task.Delay(100);
        }

        private async Task ReleaseResourcesAsync()
        {
            // Kaynakları serbest bırak;
            _shortcuts.Clear();
            _keyToShortcutId.Clear();
            _categoryBindings.Clear();

            _listeningCts?.Dispose();
            _listeningCts = null;
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnShortcutExecuted(ShortcutExecutedEventArgs e)
        {
            ShortcutExecuted?.Invoke(this, e);
        }

        protected virtual void OnShortcutRegistered(ShortcutRegisteredEventArgs e)
        {
            ShortcutRegistered?.Invoke(this, e);
        }

        protected virtual void OnShortcutConflictDetected(ShortcutConflictResult conflictResult)
        {
            var args = new ShortcutConflictEventArgs;
            {
                ConflictResult = conflictResult,
                Timestamp = DateTime.UtcNow;
            };

            ShortcutConflictDetected?.Invoke(this, args);
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _listeningCts?.Dispose();
                    _shortcuts.Clear();
                    _keyToShortcutId.Clear();
                    _categoryBindings.Clear();
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

    #region Supporting Types;

    /// <summary>
    /// Kısayol tanımı;
    /// </summary>
    public class ShortcutDefinition;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public KeyCombination KeyCombination { get; set; }
        public ShortcutType Type { get; set; }
        public string ActionReference { get; set; } // Komut ID, Makro ID veya Script yolu;
        public string Category { get; set; }
        public ShortcutContext Context { get; set; }
        public string Description { get; set; }
        public string Icon { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsGlobal { get; set; } = false;
        public ScriptType ScriptType { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Tuş kombinasyonu;
    /// </summary>
    public class KeyCombination : IEquatable<KeyCombination>
    {
        public Key Key { get; }
        public ModifierKeys Modifiers { get; }
        public bool IsMouseGesture { get; }
        public MouseGesture MouseGesture { get; }

        public KeyCombination(Key key, ModifierKeys modifiers = ModifierKeys.None)
        {
            Key = key;
            Modifiers = modifiers;
            IsMouseGesture = false;
        }

        public KeyCombination(MouseGesture gesture, ModifierKeys modifiers = ModifierKeys.None)
        {
            MouseGesture = gesture;
            Modifiers = modifiers;
            IsMouseGesture = true;
        }

        public bool Equals(KeyCombination other)
        {
            if (other == null) return false;

            if (IsMouseGesture != other.IsMouseGesture)
                return false;

            if (IsMouseGesture)
                return MouseGesture == other.MouseGesture && Modifiers == other.Modifiers;
            else;
                return Key == other.Key && Modifiers == other.Modifiers;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as KeyCombination);
        }

        public override int GetHashCode()
        {
            unchecked;
            {
                int hash = 17;
                if (IsMouseGesture)
                {
                    hash = hash * 23 + MouseGesture.GetHashCode();
                }
                else;
                {
                    hash = hash * 23 + Key.GetHashCode();
                }
                hash = hash * 23 + Modifiers.GetHashCode();
                hash = hash * 23 + IsMouseGesture.GetHashCode();
                return hash;
            }
        }

        public override string ToString()
        {
            var modifiers = new List<string>();

            if (Modifiers.HasFlag(ModifierKeys.Control))
                modifiers.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Alt))
                modifiers.Add("Alt");
            if (Modifiers.HasFlag(ModifierKeys.Shift))
                modifiers.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Windows))
                modifiers.Add("Win");

            var modifierString = string.Join(" + ", modifiers);

            if (IsMouseGesture)
            {
                return string.IsNullOrEmpty(modifierString)
                    ? MouseGesture.ToString()
                    : $"{modifierString} + {MouseGesture}";
            }
            else;
            {
                return string.IsNullOrEmpty(modifierString)
                    ? Key.ToString()
                    : $"{modifierString} + {Key}";
            }
        }
    }

    /// <summary>
    /// Kısayol yöneticisi durumu;
    /// </summary>
    public enum ShortcutManagerState;
    {
        Stopped,
        Initializing,
        Ready,
        Processing,
        Stopping,
        Error;
    }

    /// <summary>
    /// Kısayol tipi;
    /// </summary>
    public enum ShortcutType;
    {
        Command,
        Macro,
        Script,
        External,
        Custom;
    }

    /// <summary>
    /// Kısayol bağlamı;
    /// </summary>
    public enum ShortcutContext;
    {
        Global,
        Editor,
        Project,
        Debug,
        Design,
        Terminal,
        Custom;
    }

    /// <summary>
    /// Kısayol istatistikleri;
    /// </summary>
    public class ShortcutStatistics;
    {
        public DateTime StartTime { get; set; }
        public int TotalExecutions { get; set; }
        public int TotalSuccess { get; set; }
        public int TotalErrors { get; set; }
        public string MostUsedShortcut { get; set; }
        public ExecutionRecord LastExecution { get; set; }
        public Dictionary<string, int> UsageCounts { get; set; } = new Dictionary<string, int>();
    }

    /// <summary>
    /// Kısayol arama kriterleri;
    /// </summary>
    public class ShortcutSearchCriteria;
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public ShortcutContext? Context { get; set; }
        public ShortcutType? Type { get; set; }
        public bool? IsEnabled { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? ModifiedAfter { get; set; }
    }

    /// <summary>
    /// Kısayol önerisi;
    /// </summary>
    public class ShortcutSuggestion;
    {
        public string ShortcutId { get; set; }
        public string Reason { get; set; }
        public double ConfidenceScore { get; set; }
        public SuggestionType Type { get; set; }
        public string Recommendation { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// Kısayol yöneticisi arayüzü;
    /// </summary>
    public interface IShortcutManager : IDisposable
    {
        Task InitializeAsync(ShortcutConfiguration configuration = null);
        ShortcutRegistrationResult RegisterShortcut(ShortcutDefinition shortcut);
        void SwitchContext(ShortcutContext newContext);
        Task<ShortcutExecutionResult> ExecuteShortcutAsync(string shortcutId, ExecutionParameters parameters = null, CancellationToken cancellationToken = default);
        Task<ShortcutExecutionResult> ProcessKeyCombinationAsync(KeyCombination keyCombination, ExecutionParameters parameters = null, CancellationToken cancellationToken = default);
        IEnumerable<ShortcutDefinition> SearchShortcuts(ShortcutSearchCriteria criteria, ShortcutSearchOptions options = null);
        IEnumerable<ShortcutGroup> GetShortcutGroups();
        Task<IEnumerable<ShortcutSuggestion>> GenerateSuggestionsAsync(UsagePattern pattern, SuggestionOptions options = null);
        Task SaveConfigurationAsync();
        Task ShutdownAsync();
    }

    #endregion;

    #region Exceptions;

    /// <summary>
    /// Kısayol yöneticisi istisnası;
    /// </summary>
    public class ShortcutManagerException : Exception
    {
        public ShortcutManagerException() { }
        public ShortcutManagerException(string message) : base(message) { }
        public ShortcutManagerException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Kısayol kayıt istisnası;
    /// </summary>
    public class ShortcutRegistrationException : ShortcutManagerException;
    {
        public ShortcutRegistrationException() { }
        public ShortcutRegistrationException(string message) : base(message) { }
        public ShortcutRegistrationException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Kısayol yürütme istisnası;
    /// </summary>
    public class ShortcutExecutionException : ShortcutManagerException;
    {
        public ShortcutExecutionException() { }
        public ShortcutExecutionException(string message) : base(message) { }
        public ShortcutExecutionException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;
}
