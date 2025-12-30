using MaterialDesignThemes.Wpf;
using NEDA.CharacterSystems.CharacterCreator.MorphTargets;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Security;
using NEDA.Interface.TextInput.Models;
using NEDA.Interface.TextInput.QuickCommands.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NEDA.Interface.TextInput.QuickCommands;{
    /// <summary>
    /// Profesyonel makro ve otomasyon script oynatıcısı;
    /// Kayıtlı makroları çalıştırır, script dosyalarını yürütür ve otomasyon iş akışlarını yönetir;
    /// </summary>
    public class AutomationPlayer : IAutomationPlayer, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly IMacroRecorder _macroRecorder;
        private readonly IScriptEngine _scriptEngine;

        private readonly object _syncLock = new object();
        private bool _isPlaying;
        private bool _isPaused;
        private bool _isDisposed;
        private CancellationTokenSource _playbackCancellationTokenSource;
        private Thread _playbackThread;

        // Oynatma durumu ve istatistikler;
        private PlaybackSession _currentSession;
        private readonly Stopwatch _playbackStopwatch = new Stopwatch();
        private int _currentStepIndex;
        private AutomationScript _currentScript;

        // Çevre ve bağlam;
        private readonly Dictionary<string, object> _environment = new Dictionary<string, object>();
        private readonly Dictionary<string, object> _variables = new Dictionary<string, object>();
        private readonly Stack<PlaybackContext> _contextStack = new Stack<PlaybackContext>();

        // Plugin ve uzantılar;
        private readonly List<IAutomationPlugin> _plugins = new List<IAutomationPlugin>();
        private readonly Dictionary<string, IAutomationCommand> _registeredCommands = new Dictionary<string, IAutomationCommand>();

        // Hata yönetimi ve geri alma;
        private readonly Stack<PlaybackAction> _undoStack = new Stack<PlaybackAction>();
        private readonly Stack<PlaybackAction> _redoStack = new Stack<PlaybackAction>();
        private readonly List<RecoveryPoint> _recoveryPoints = new List<RecoveryPoint>();

        // Performans izleme;
        private readonly PerformanceMonitor _performanceMonitor;

        // Olaylar (Events)
        public event EventHandler<PlaybackStartedEventArgs> PlaybackStarted;
        public event EventHandler<PlaybackStoppedEventArgs> PlaybackStopped;
        public event EventHandler<PlaybackPausedEventArgs> PlaybackPaused;
        public event EventHandler<PlaybackResumedEventArgs> PlaybackResumed;
        public event EventHandler<StepExecutedEventArgs> StepExecuted;
        public event EventHandler<StepFailedEventArgs> StepFailed;
        public event EventHandler<PlaybackErrorEventArgs> PlaybackError;
        public event EventHandler<PlaybackProgressEventArgs> PlaybackProgress;
        public event EventHandler<VariableChangedEventArgs> VariableChanged;
        public event EventHandler<BreakpointHitEventArgs> BreakpointHit;

        /// <summary>
        /// AutomationPlayer constructor;
        /// </summary>
        public AutomationPlayer(
            ILogger logger = null,
            ISecurityManager securityManager = null,
            IMacroRecorder macroRecorder = null,
            IScriptEngine scriptEngine = null)
        {
            _logger = logger ?? LogManager.GetLogger(typeof(AutomationPlayer));
            _securityManager = securityManager ?? SecurityManager.Instance;
            _macroRecorder = macroRecorder;
            _scriptEngine = scriptEngine ?? new ScriptEngine();

            _performanceMonitor = new PerformanceMonitor();

            // Varsayılan komutları kaydet;
            RegisterDefaultCommands();

            // Çevre değişkenlerini başlat;
            InitializeEnvironment();

            _logger.Info("AutomationPlayer başlatıldı");
        }

        /// <summary>
        /// Constructor with configuration;
        /// </summary>
        public AutomationPlayer(AutomationPlayerConfig config, ILogger logger = null)
            : this(logger)
        {
            if (config != null)
            {
                ApplyConfiguration(config);
            }
        }

        #region Public Properties;

        /// <summary>
        /// Oynatma durumu;
        /// </summary>
        public bool IsPlaying;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _isPlaying;
                }
            }
        }

        /// <summary>
        /// Duraklatma durumu;
        /// </summary>
        public bool IsPaused;
        {
            get;
            {
                lock (_syncLock)
                {
                    return _isPaused;
                }
            }
        }

        /// <summary>
        /// Geçerli oturum;
        /// </summary>
        public PlaybackSession CurrentSession => _currentSession;

        /// <summary>
        /// Geçerli script;
        /// </summary>
        public AutomationScript CurrentScript => _currentScript;

        /// <summary>
        /// Geçerli adım indeksi;
        /// </summary>
        public int CurrentStepIndex => _currentStepIndex;

        /// <summary>
        /// Toplam adım sayısı;
        /// </summary>
        public int TotalSteps => _currentScript?.Steps?.Count ?? 0;

        /// <summary>
        /// Geçen süre;
        /// </summary>
        public TimeSpan ElapsedTime => _playbackStopwatch.Elapsed;

        /// <summary>
        /// Değişkenler;
        /// </summary>
        public IReadOnlyDictionary<string, object> Variables => _variables.AsReadOnly();

        /// <summary>
        /// Performans istatistikleri;
        /// </summary>
        public PerformanceStats PerformanceStats => _performanceMonitor.GetStats();

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Makro oynat;
        /// </summary>
        public async Task<PlaybackResult> PlayMacroAsync(
            RecordedMacro macro,
            PlaybackOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(macro, nameof(macro));

            var playbackOptions = options ?? new PlaybackOptions();
            var result = new PlaybackResult { PlaybackId = Guid.NewGuid().ToString() };

            try
            {
                // Güvenlik kontrolü;
                if (!await CheckSecurityAsync(macro, SecurityAction.Playback))
                {
                    result.Status = PlaybackStatus.SecurityDenied;
                    result.Error = "Bu makroyu oynatmak için yetkiniz yok";
                    return result;
                }

                // Script oluştur;
                var script = ConvertMacroToScript(macro, playbackOptions);

                // Script'i oynat;
                return await PlayScriptAsync(script, playbackOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Makro oynatma hatası: {ex.Message}", ex);
                result.Status = PlaybackStatus.Error;
                result.Error = $"Makro oynatma hatası: {ex.Message}";
                result.Exception = ex;
                return result;
            }
        }

        /// <summary>
        /// Script oynat;
        /// </summary>
        public async Task<PlaybackResult> PlayScriptAsync(
            AutomationScript script,
            PlaybackOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNull(script, nameof(script));

            var playbackOptions = options ?? new PlaybackOptions();
            var result = new PlaybackResult;
            {
                PlaybackId = Guid.NewGuid().ToString(),
                ScriptName = script.Name;
            };

            lock (_syncLock)
            {
                if (_isPlaying)
                {
                    result.Status = PlaybackStatus.AlreadyPlaying;
                    result.Error = "Zaten bir oynatma işlemi devam ediyor";
                    return result;
                }

                _isPlaying = true;
                _isPaused = false;
                _currentStepIndex = 0;
                _currentScript = script;

                _currentSession = new PlaybackSession;
                {
                    SessionId = result.PlaybackId,
                    StartTime = DateTime.Now,
                    ScriptName = script.Name,
                    Options = playbackOptions;
                };
            }

            // Oynatma başlatma event'i;
            OnPlaybackStarted(new PlaybackStartedEventArgs;
            {
                Session = _currentSession,
                Timestamp = DateTime.UtcNow;
            });

            // Geri alma stack'ini temizle;
            _undoStack.Clear();
            _redoStack.Clear();

            // Oynatma thread'ini başlat;
            _playbackCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _playbackThread = new Thread(async () =>
            {
                await ExecutePlaybackAsync(script, playbackOptions, result, _playbackCancellationTokenSource.Token);
            })
            {
                Name = $"AutomationPlayer-{result.PlaybackId}",
                IsBackground = true;
            };

            _playbackThread.Start();

            return result;
        }

        /// <summary>
        /// Script dosyası oynat;
        /// </summary>
        public async Task<PlaybackResult> PlayScriptFileAsync(
            string filePath,
            PlaybackOptions options = null,
            CancellationToken cancellationToken = default)
        {
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Script dosyası bulunamadı: {filePath}");
            }

            try
            {
                // Script dosyasını yükle;
                var script = await LoadScriptFromFileAsync(filePath);

                // Oynat;
                return await PlayScriptAsync(script, options, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Script dosyası oynatma hatası: {ex.Message}", ex);
                throw new AutomationPlaybackException($"Script dosyası oynatılamadı: {filePath}", ex);
            }
        }

        /// <summary>
        /// Oynatmayı duraklat;
        /// </summary>
        public void Pause()
        {
            lock (_syncLock)
            {
                if (!_isPlaying || _isPaused)
                {
                    return;
                }

                _isPaused = true;
                _playbackStopwatch.Stop();

                _logger.Info("Oynatma duraklatıldı");

                OnPlaybackPaused(new PlaybackPausedEventArgs;
                {
                    Session = _currentSession,
                    CurrentStep = _currentStepIndex,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Oynatmayı devam ettir;
        /// </summary>
        public void Resume()
        {
            lock (_syncLock)
            {
                if (!_isPlaying || !_isPaused)
                {
                    return;
                }

                _isPaused = false;
                _playbackStopwatch.Start();

                _logger.Info("Oynatma devam ettiriliyor");

                OnPlaybackResumed(new PlaybackResumedEventArgs;
                {
                    Session = _currentSession,
                    CurrentStep = _currentStepIndex,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Oynatmayı durdur;
        /// </summary>
        public void Stop()
        {
            lock (_syncLock)
            {
                if (!_isPlaying)
                {
                    return;
                }

                _playbackCancellationTokenSource?.Cancel();

                _isPlaying = false;
                _isPaused = false;
                _playbackStopwatch.Stop();

                // Thread'in bitmesini bekle;
                if (_playbackThread != null && _playbackThread.IsAlive)
                {
                    _playbackThread.Join(TimeSpan.FromSeconds(5));
                }

                _logger.Info("Oynatma durduruldu");

                OnPlaybackStopped(new PlaybackStoppedEventArgs;
                {
                    Session = _currentSession,
                    CurrentStep = _currentStepIndex,
                    ElapsedTime = _playbackStopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow;
                });

                // Oturumu sonlandır;
                if (_currentSession != null)
                {
                    _currentSession.EndTime = DateTime.Now;
                    _currentSession.Status = PlaybackSessionStatus.Stopped;
                }

                // Temizlik;
                CleanupPlayback();
            }
        }

        /// <summary>
        /// Bir sonraki adıma geç;
        /// </summary>
        public async Task<bool> StepNextAsync()
        {
            lock (_syncLock)
            {
                if (!_isPlaying || _currentScript == null || _currentStepIndex >= _currentScript.Steps.Count)
                {
                    return false;
                }
            }

            try
            {
                // Geçerli adımı çalıştır;
                var step = _currentScript.Steps[_currentStepIndex];
                var success = await ExecuteStepAsync(step, _playbackCancellationTokenSource?.Token ?? CancellationToken.None);

                if (success)
                {
                    _currentStepIndex++;

                    // İlerleme event'i;
                    OnPlaybackProgress(new PlaybackProgressEventArgs;
                    {
                        Session = _currentSession,
                        CurrentStep = _currentStepIndex,
                        TotalSteps = _currentScript.Steps.Count,
                        ProgressPercentage = (_currentStepIndex * 100) / _currentScript.Steps.Count,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.Error($"Adım ilerleme hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Belirli bir adıma git;
        /// </summary>
        public async Task<bool> GoToStepAsync(int stepIndex)
        {
            lock (_syncLock)
            {
                if (!_isPlaying || _currentScript == null || stepIndex < 0 || stepIndex >= _currentScript.Steps.Count)
                {
                    return false;
                }

                _currentStepIndex = stepIndex;
            }

            // İlerleme event'i;
            OnPlaybackProgress(new PlaybackProgressEventArgs;
            {
                Session = _currentSession,
                CurrentStep = _currentStepIndex,
                TotalSteps = _currentScript.Steps.Count,
                ProgressPercentage = (_currentStepIndex * 100) / _currentScript.Steps.Count,
                Timestamp = DateTime.UtcNow;
            });

            return true;
        }

        /// <summary>
        /// Değişken ayarla;
        /// </summary>
        public void SetVariable(string name, object value)
        {
            Guard.ArgumentNotNullOrEmpty(name, nameof(name));

            lock (_syncLock)
            {
                var oldValue = _variables.ContainsKey(name) ? _variables[name] : null;
                _variables[name] = value;

                _logger.Debug($"Değişken ayarlandı: {name}={value}");

                OnVariableChanged(new VariableChangedEventArgs;
                {
                    VariableName = name,
                    OldValue = oldValue,
                    NewValue = value,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Değişken al;
        /// </summary>
        public object GetVariable(string name)
        {
            Guard.ArgumentNotNullOrEmpty(name, nameof(name));

            lock (_syncLock)
            {
                return _variables.TryGetValue(name, out var value) ? value : null;
            }
        }

        /// <summary>
        /// Komut kaydet;
        /// </summary>
        public bool RegisterCommand(string commandName, IAutomationCommand command)
        {
            Guard.ArgumentNotNullOrEmpty(commandName, nameof(commandName));
            Guard.ArgumentNotNull(command, nameof(command));

            lock (_syncLock)
            {
                if (_registeredCommands.ContainsKey(commandName))
                {
                    _logger.Warn($"Komut zaten kayıtlı: {commandName}");
                    return false;
                }

                _registeredCommands[commandName] = command;

                _logger.Info($"Komut kaydedildi: {commandName}");
                return true;
            }
        }

        /// <summary>
        /// Plugin kaydet;
        /// </summary>
        public bool RegisterPlugin(IAutomationPlugin plugin)
        {
            Guard.ArgumentNotNull(plugin, nameof(plugin));

            lock (_syncLock)
            {
                try
                {
                    plugin.Initialize(this);
                    _plugins.Add(plugin);

                    _logger.Info($"Plugin kaydedildi: {plugin.Name}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error($"Plugin başlatma hatası: {ex.Message}", ex);
                    return false;
                }
            }
        }

        /// <summary>
        /// Breakpoint ekle;
        /// </summary>
        public void AddBreakpoint(int stepIndex, BreakpointCondition condition = null)
        {
            Guard.ArgumentInRange(stepIndex, 0, int.MaxValue, nameof(stepIndex));

            lock (_syncLock)
            {
                var breakpoint = new Breakpoint;
                {
                    StepIndex = stepIndex,
                    Condition = condition,
                    IsEnabled = true;
                };

                if (_currentSession != null)
                {
                    _currentSession.Breakpoints.Add(breakpoint);
                }

                _logger.Debug($"Breakpoint eklendi: {stepIndex}");
            }
        }

        /// <summary>
        /// Geri al;
        /// </summary>
        public async Task<bool> UndoAsync()
        {
            lock (_syncLock)
            {
                if (_undoStack.Count == 0)
                {
                    return false;
                }

                var action = _undoStack.Pop();
                _redoStack.Push(action);

                return UndoAction(action);
            }
        }

        /// <summary>
        /// Yinele;
        /// </summary>
        public async Task<bool> RedoAsync()
        {
            lock (_syncLock)
            {
                if (_redoStack.Count == 0)
                {
                    return false;
                }

                var action = _redoStack.Pop();
                _undoStack.Push(action);

                return RedoAction(action);
            }
        }

        /// <summary>
        /// Kayıt noktası oluştur;
        /// </summary>
        public RecoveryPoint CreateRecoveryPoint(string name = null)
        {
            lock (_syncLock)
            {
                var recoveryPoint = new RecoveryPoint;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name ?? $"RecoveryPoint_{DateTime.Now:yyyyMMdd_HHmmss}",
                    Timestamp = DateTime.Now,
                    Variables = new Dictionary<string, object>(_variables),
                    Environment = new Dictionary<string, object>(_environment),
                    CurrentStep = _currentStepIndex;
                };

                _recoveryPoints.Add(recoveryPoint);

                _logger.Debug($"Kayıt noktası oluşturuldu: {recoveryPoint.Name}");
                return recoveryPoint;
            }
        }

        /// <summary>
        /// Kayıt noktasına dön;
        /// </summary>
        public async Task<bool> RestoreToRecoveryPointAsync(string recoveryPointId)
        {
            lock (_syncLock)
            {
                var recoveryPoint = _recoveryPoints.FirstOrDefault(rp => rp.Id == recoveryPointId);
                if (recoveryPoint == null)
                {
                    return false;
                }

                // Değişkenleri geri yükle;
                _variables.Clear();
                foreach (var kvp in recoveryPoint.Variables)
                {
                    _variables[kvp.Key] = kvp.Value;
                }

                // Çevreyi geri yükle;
                _environment.Clear();
                foreach (var kvp in recoveryPoint.Environment)
                {
                    _environment[kvp.Key] = kvp.Value;
                }

                // Adımı geri yükle;
                _currentStepIndex = recoveryPoint.CurrentStep;

                _logger.Info($"Kayıt noktasına dönüldü: {recoveryPoint.Name}");
                return true;
            }
        }

        /// <summary>
        /// Script dosyasını yükle;
        /// </summary>
        public async Task<AutomationScript> LoadScriptFromFileAsync(string filePath)
        {
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));

            try
            {
                var fileContent = await File.ReadAllTextAsync(filePath);
                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();

                switch (fileExtension)
                {
                    case ".json":
                        return JsonConvert.DeserializeObject<AutomationScript>(fileContent);

                    case ".xml":
                        return LoadScriptFromXml(fileContent);

                    case ".nedascript":
                        return LoadNedaScript(fileContent);

                    default:
                        throw new NotSupportedException($"Desteklenmeyen script formatı: {fileExtension}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Script yükleme hatası: {ex.Message}", ex);
                throw new AutomationScriptException($"Script yüklenemedi: {filePath}", ex);
            }
        }

        /// <summary>
        /// Script'i dosyaya kaydet;
        /// </summary>
        public async Task SaveScriptToFileAsync(AutomationScript script, string filePath)
        {
            Guard.ArgumentNotNull(script, nameof(script));
            Guard.ArgumentNotNullOrEmpty(filePath, nameof(filePath));

            try
            {
                var fileExtension = Path.GetExtension(filePath).ToLowerInvariant();
                string fileContent;

                switch (fileExtension)
                {
                    case ".json":
                        fileContent = JsonConvert.SerializeObject(script, Formatting.Indented);
                        break;

                    case ".xml":
                        fileContent = ConvertScriptToXml(script);
                        break;

                    default:
                        throw new NotSupportedException($"Desteklenmeyen script formatı: {fileExtension}");
                }

                await File.WriteAllTextAsync(filePath, fileContent);

                _logger.Info($"Script kaydedildi: {filePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Script kaydetme hatası: {ex.Message}", ex);
                throw new AutomationScriptException($"Script kaydedilemedi: {filePath}", ex);
            }
        }

        /// <summary>
        /// Oynatma istatistiklerini al;
        /// </summary>
        public PlaybackStats GetPlaybackStats()
        {
            return new PlaybackStats;
            {
                TotalPlaybackTime = _performanceMonitor.GetTotalPlaybackTime(),
                AverageStepTime = _performanceMonitor.GetAverageStepTime(),
                TotalStepsExecuted = _performanceMonitor.TotalStepsExecuted,
                SuccessfulSteps = _performanceMonitor.SuccessfulSteps,
                FailedSteps = _performanceMonitor.FailedSteps,
                LastPlaybackSession = _currentSession;
            };
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Oynatma işlemini yürüt;
        /// </summary>
        private async Task ExecutePlaybackAsync(
            AutomationScript script,
            PlaybackOptions options,
            PlaybackResult result,
            CancellationToken cancellationToken)
        {
            _playbackStopwatch.Restart();

            try
            {
                _performanceMonitor.StartMonitoring();

                // Plugin ön işleme;
                foreach (var plugin in _plugins)
                {
                    script = await plugin.PreprocessScriptAsync(script) ?? script;
                }

                // Değişkenleri başlat;
                InitializeVariables(script);

                // Ana döngü;
                for (_currentStepIndex = 0; _currentStepIndex < script.Steps.Count; _currentStepIndex++)
                {
                    // İptal kontrolü;
                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Status = PlaybackStatus.Cancelled;
                        break;
                    }

                    // Duraklatma kontrolü;
                    while (_isPaused && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(100, cancellationToken);
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Status = PlaybackStatus.Cancelled;
                        break;
                    }

                    // Breakpoint kontrolü;
                    if (CheckBreakpoint(_currentStepIndex))
                    {
                        OnBreakpointHit(new BreakpointHitEventArgs;
                        {
                            StepIndex = _currentStepIndex,
                            Step = script.Steps[_currentStepIndex],
                            Timestamp = DateTime.UtcNow;
                        });

                        // Breakpoint'te duraklat;
                        _isPaused = true;
                        continue;
                    }

                    // Adımı çalıştır;
                    var step = script.Steps[_currentStepIndex];
                    var stepResult = await ExecuteStepAsync(step, cancellationToken);

                    if (stepResult)
                    {
                        _performanceMonitor.RecordSuccessfulStep();
                    }
                    else;
                    {
                        _performanceMonitor.RecordFailedStep();

                        // Hata yönetimi;
                        if (options.StopOnError)
                        {
                            result.Status = PlaybackStatus.Error;
                            result.FailedStep = _currentStepIndex;
                            break;
                        }

                        // Hata toleransı;
                        if (options.ErrorTolerance > 0)
                        {
                            // Implement error tolerance logic;
                        }
                    }

                    // Gecikme;
                    if (options.StepDelay > 0)
                    {
                        await Task.Delay(options.StepDelay, cancellationToken);
                    }

                    // İlerleme event'i;
                    OnPlaybackProgress(new PlaybackProgressEventArgs;
                    {
                        Session = _currentSession,
                        CurrentStep = _currentStepIndex,
                        TotalSteps = script.Steps.Count,
                        ProgressPercentage = ((_currentStepIndex + 1) * 100) / script.Steps.Count,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Başarı durumu;
                if (result.Status == PlaybackStatus.None)
                {
                    result.Status = PlaybackStatus.Completed;
                }

                result.TotalSteps = script.Steps.Count;
                result.ExecutedSteps = _currentStepIndex;
                result.ElapsedTime = _playbackStopwatch.Elapsed;
            }
            catch (OperationCanceledException)
            {
                result.Status = PlaybackStatus.Cancelled;
                result.Error = "Oynatma iptal edildi";
            }
            catch (Exception ex)
            {
                result.Status = PlaybackStatus.Error;
                result.Error = $"Oynatma hatası: {ex.Message}";
                result.Exception = ex;

                OnPlaybackError(new PlaybackErrorEventArgs;
                {
                    Session = _currentSession,
                    StepIndex = _currentStepIndex,
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                _performanceMonitor.StopMonitoring();
                _playbackStopwatch.Stop();

                // Oturumu güncelle;
                if (_currentSession != null)
                {
                    _currentSession.EndTime = DateTime.Now;
                    _currentSession.Status = result.Status == PlaybackStatus.Completed ?
                        PlaybackSessionStatus.Completed : PlaybackSessionStatus.Error;
                    _currentSession.Result = result;
                }

                // Plugin son işleme;
                foreach (var plugin in _plugins)
                {
                    await plugin.PostprocessScriptAsync(script, result);
                }

                // Oynatma durdurma event'i;
                OnPlaybackStopped(new PlaybackStoppedEventArgs;
                {
                    Session = _currentSession,
                    CurrentStep = _currentStepIndex,
                    ElapsedTime = _playbackStopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow;
                });

                lock (_syncLock)
                {
                    _isPlaying = false;
                    _isPaused = false;
                }

                _logger.Info($"Oynatma tamamlandı: {result.Status}");
            }
        }

        /// <summary>
        /// Adımı çalıştır;
        /// </summary>
        private async Task<bool> ExecuteStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            try
            {
                // Değişkenleri genişlet;
                var expandedStep = ExpandVariablesInStep(step);

                // Plugin ön işleme;
                foreach (var plugin in _plugins)
                {
                    expandedStep = await plugin.PreprocessStepAsync(expandedStep) ?? expandedStep;
                }

                // Adım türüne göre işle;
                bool result = false;

                switch (expandedStep.StepType)
                {
                    case StepType.Command:
                        result = await ExecuteCommandStepAsync(expandedStep, cancellationToken);
                        break;

                    case StepType.Script:
                        result = await ExecuteScriptStepAsync(expandedStep, cancellationToken);
                        break;

                    case StepType.Condition:
                        result = await ExecuteConditionStepAsync(expandedStep, cancellationToken);
                        break;

                    case StepType.Loop:
                        result = await ExecuteLoopStepAsync(expandedStep, cancellationToken);
                        break;

                    case StepType.Action:
                        result = await ExecuteActionStepAsync(expandedStep, cancellationToken);
                        break;

                    case StepType.Wait:
                        result = await ExecuteWaitStepAsync(expandedStep, cancellationToken);
                        break;

                    default:
                        throw new NotSupportedException($"Desteklenmeyen adım türü: {expandedStep.StepType}");
                }

                // Geri alma eylemi kaydet;
                if (result && expandedStep.SupportsUndo)
                {
                    var action = CreateUndoAction(expandedStep);
                    if (action != null)
                    {
                        _undoStack.Push(action);
                        _redoStack.Clear();
                    }
                }

                // Event tetikle;
                OnStepExecuted(new StepExecutedEventArgs;
                {
                    Step = expandedStep,
                    StepIndex = _currentStepIndex,
                    Success = result,
                    Timestamp = DateTime.UtcNow;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Adım çalıştırma hatası: {ex.Message}", ex);

                OnStepFailed(new StepFailedEventArgs;
                {
                    Step = step,
                    StepIndex = _currentStepIndex,
                    Exception = ex,
                    Timestamp = DateTime.UtcNow;
                });

                return false;
            }
        }

        /// <summary>
        /// Komut adımını çalıştır;
        /// </summary>
        private async Task<bool> ExecuteCommandStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            if (!_registeredCommands.TryGetValue(step.Command, out var command))
            {
                throw new AutomationCommandException($"Komut bulunamadı: {step.Command}");
            }

            // Parametreleri hazırla;
            var parameters = new Dictionary<string, object>();
            foreach (var param in step.Parameters)
            {
                parameters[param.Key] = param.Value;
            }

            // Komutu çalıştır;
            var context = new CommandContext;
            {
                Step = step,
                Variables = _variables,
                Environment = _environment,
                CancellationToken = cancellationToken;
            };

            return await command.ExecuteAsync(parameters, context);
        }

        /// <summary>
        /// Script adımını çalıştır;
        /// </summary>
        private async Task<bool> ExecuteScriptStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            if (_scriptEngine == null)
            {
                throw new InvalidOperationException("Script motoru bulunamadı");
            }

            var scriptContent = step.Parameters.GetValueOrDefault("script", "")?.ToString();
            if (string.IsNullOrEmpty(scriptContent))
            {
                throw new ArgumentException("Script içeriği boş");
            }

            // Script'i çalıştır;
            var scriptResult = await _scriptEngine.ExecuteAsync(scriptContent, _variables, cancellationToken);

            // Sonucu değişkenlere kaydet;
            if (scriptResult.Success && !string.IsNullOrEmpty(step.ResultVariable))
            {
                SetVariable(step.ResultVariable, scriptResult.Result);
            }

            return scriptResult.Success;
        }

        /// <summary>
        /// Koşul adımını çalıştır;
        /// </summary>
        private async Task<bool> ExecuteConditionStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            var condition = step.Parameters.GetValueOrDefault("condition", "")?.ToString();
            if (string.IsNullOrEmpty(condition))
            {
                throw new ArgumentException("Koşul ifadesi boş");
            }

            // Koşulu değerlendir;
            var evaluator = new ConditionEvaluator();
            var result = evaluator.Evaluate(condition, _variables);

            // Koşul sonucuna göre işlem;
            if (result && step.Parameters.TryGetValue("then", out var thenStep))
            {
                // Then adımını çalıştır;
            }
            else if (!result && step.Parameters.TryGetValue("else", out var elseStep))
            {
                // Else adımını çalıştır;
            }

            return true;
        }

        /// <summary>
        /// Döngü adımını çalıştır;
        /// </summary>
        private async Task<bool> ExecuteLoopStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            var loopType = step.Parameters.GetValueOrDefault("type", "for")?.ToString().ToLowerInvariant();
            var iterations = Convert.ToInt32(step.Parameters.GetValueOrDefault("iterations", 1));
            var items = step.Parameters.GetValueOrDefault("items", null);

            switch (loopType)
            {
                case "for":
                    for (int i = 0; i < iterations; i++)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        SetVariable("loop_index", i);
                        SetVariable("loop_iteration", i + 1);

                        // İç adımları çalıştır;
                        if (step.ChildSteps != null)
                        {
                            foreach (var childStep in step.ChildSteps)
                            {
                                await ExecuteStepAsync(childStep, cancellationToken);
                            }
                        }
                    }
                    break;

                case "foreach":
                    if (items is IEnumerable<object> enumerable)
                    {
                        int index = 0;
                        foreach (var item in enumerable)
                        {
                            if (cancellationToken.IsCancellationRequested)
                                break;

                            SetVariable("loop_item", item);
                            SetVariable("loop_index", index);
                            SetVariable("loop_iteration", index + 1);

                            // İç adımları çalıştır;
                            if (step.ChildSteps != null)
                            {
                                foreach (var childStep in step.ChildSteps)
                                {
                                    await ExecuteStepAsync(childStep, cancellationToken);
                                }
                            }

                            index++;
                        }
                    }
                    break;

                case "while":
                    var condition = step.Parameters.GetValueOrDefault("condition", "")?.ToString();
                    var evaluator = new ConditionEvaluator();

                    while (evaluator.Evaluate(condition, _variables))
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // İç adımları çalıştır;
                        if (step.ChildSteps != null)
                        {
                            foreach (var childStep in step.ChildSteps)
                            {
                                await ExecuteStepAsync(childStep, cancellationToken);
                            }
                        }
                    }
                    break;
            }

            return true;
        }

        /// <summary>
        /// Aksiyon adımını çalıştır;
        /// </summary>
        private async Task<bool> ExecuteActionStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            // UI otomasyonu, dosya işlemleri, vs.
            var actionType = step.Parameters.GetValueOrDefault("action", "")?.ToString().ToLowerInvariant();

            switch (actionType)
            {
                case "click":
                    // UI elementine tıkla;
                    break;

                case "type":
                    // Klavye girişi;
                    break;

                case "wait":
                    var delay = Convert.ToInt32(step.Parameters.GetValueOrDefault("delay", 1000));
                    await Task.Delay(delay, cancellationToken);
                    break;

                case "file_copy":
                    // Dosya kopyala;
                    break;

                case "file_delete":
                    // Dosya sil;
                    break;

                default:
                    throw new NotSupportedException($"Desteklenmeyen aksiyon: {actionType}");
            }

            return true;
        }

        /// <summary>
        /// Bekleme adımını çalıştır;
        /// </summary>
        private async Task<bool> ExecuteWaitStepAsync(AutomationStep step, CancellationToken cancellationToken)
        {
            var delay = Convert.ToInt32(step.Parameters.GetValueOrDefault("delay", 1000));
            var condition = step.Parameters.GetValueOrDefault("condition", "")?.ToString();

            if (!string.IsNullOrEmpty(condition))
            {
                // Koşul sağlanana kadar bekle;
                var evaluator = new ConditionEvaluator();
                var timeout = delay;
                var interval = 100; // ms;

                while (timeout > 0)
                {
                    if (evaluator.Evaluate(condition, _variables))
                    {
                        return true;
                    }

                    await Task.Delay(interval, cancellationToken);
                    timeout -= interval;
                }

                return false;
            }
            else;
            {
                // Sabit gecikme;
                await Task.Delay(delay, cancellationToken);
                return true;
            }
        }

        /// <summary>
        /// Adımdaki değişkenleri genişlet;
        /// </summary>
        private AutomationStep ExpandVariablesInStep(AutomationStep step)
        {
            var expandedStep = step.Clone();

            // Komut adını genişlet;
            if (!string.IsNullOrEmpty(expandedStep.Command))
            {
                expandedStep.Command = ExpandVariables(expandedStep.Command);
            }

            // Parametreleri genişlet;
            foreach (var key in expandedStep.Parameters.Keys.ToList())
            {
                var value = expandedStep.Parameters[key];
                if (value is string stringValue)
                {
                    expandedStep.Parameters[key] = ExpandVariables(stringValue);
                }
            }

            // Açıklamayı genişlet;
            if (!string.IsNullOrEmpty(expandedStep.Description))
            {
                expandedStep.Description = ExpandVariables(expandedStep.Description);
            }

            return expandedStep;
        }

        /// <summary>
        /// Değişkenleri genişlet;
        /// </summary>
        private string ExpandVariables(string input)
        {
            if (string.IsNullOrEmpty(input))
                return input;

            var result = input;
            var regex = new Regex(@"\$\{(\w+)\}|\$(\w+)");

            return regex.Replace(result, match =>
            {
                var varName = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;

                if (_variables.TryGetValue(varName, out var value))
                {
                    return value?.ToString() ?? string.Empty;
                }

                if (_environment.TryGetValue(varName, out var envValue))
                {
                    return envValue?.ToString() ?? string.Empty;
                }

                return match.Value;
            });
        }

        /// <summary>
        /// Breakpoint kontrolü;
        /// </summary>
        private bool CheckBreakpoint(int stepIndex)
        {
            if (_currentSession == null || !_currentSession.Breakpoints.Any())
                return false;

            var breakpoint = _currentSession.Breakpoints.FirstOrDefault(bp =>
                bp.StepIndex == stepIndex && bp.IsEnabled);

            if (breakpoint == null)
                return false;

            // Koşul kontrolü;
            if (breakpoint.Condition != null)
            {
                var evaluator = new ConditionEvaluator();
                return evaluator.Evaluate(breakpoint.Condition.Expression, _variables);
            }

            return true;
        }

        /// <summary>
        /// Geri alma eylemi oluştur;
        /// </summary>
        private PlaybackAction CreateUndoAction(AutomationStep step)
        {
            // Adım türüne göre geri alma eylemi oluştur;
            switch (step.StepType)
            {
                case StepType.Command:
                    return new PlaybackAction;
                    {
                        ActionType = ActionType.Command,
                        Step = step,
                        Timestamp = DateTime.Now;
                    };

                case StepType.Action:
                    return new PlaybackAction;
                    {
                        ActionType = ActionType.UIAction,
                        Step = step,
                        Timestamp = DateTime.Now;
                    };

                default:
                    return null;
            }
        }

        /// <summary>
        /// Geri alma eylemini uygula;
        /// </summary>
        private bool UndoAction(PlaybackAction action)
        {
            try
            {
                switch (action.ActionType)
                {
                    case ActionType.Command:
                        // Komut geri al;
                        break;

                    case ActionType.UIAction:
                        // UI aksiyonu geri al;
                        break;

                    case ActionType.FileOperation:
                        // Dosya işlemi geri al;
                        break;
                }

                _logger.Debug($"Geri al eylemi uygulandı: {action.ActionType}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"Geri alma hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Yineleme eylemini uygula;
        /// </summary>
        private bool RedoAction(PlaybackAction action)
        {
            try
            {
                // Adımı yeniden çalıştır;
                var task = ExecuteStepAsync(action.Step, CancellationToken.None);
                task.Wait();

                _logger.Debug($"Yineleme eylemi uygulandı: {action.ActionType}");
                return task.Result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Yineleme hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Makroyu script'e dönüştür;
        /// </summary>
        private AutomationScript ConvertMacroToScript(RecordedMacro macro, PlaybackOptions options)
        {
            var script = new AutomationScript;
            {
                Id = macro.Id,
                Name = macro.Name,
                Version = macro.Version,
                Description = macro.Description,
                Created = macro.Created,
                Modified = macro.Modified,
                Author = macro.Author;
            };

            foreach (var recordedAction in macro.Actions)
            {
                var step = new AutomationStep;
                {
                    Id = Guid.NewGuid().ToString(),
                    StepType = StepType.Action,
                    Command = recordedAction.ActionType.ToString(),
                    Description = recordedAction.Description,
                    Timestamp = recordedAction.Timestamp;
                };

                // Parametreleri ayarla;
                foreach (var param in recordedAction.Parameters)
                {
                    step.Parameters[param.Key] = param.Value;
                }

                script.Steps.Add(step);
            }

            return script;
        }

        /// <summary>
        /// Çevreyi başlat;
        /// </summary>
        private void InitializeEnvironment()
        {
            _environment["OS"] = Environment.OSVersion.Platform.ToString();
            _environment["MachineName"] = Environment.MachineName;
            _environment["UserName"] = Environment.UserName;
            _environment["CurrentDirectory"] = Environment.CurrentDirectory;
            _environment["DateTime"] = DateTime.Now;
            _environment["Timestamp"] = DateTimeOffset.Now.ToUnixTimeSeconds();
        }

        /// <summary>
        /// Değişkenleri başlat;
        /// </summary>
        private void InitializeVariables(AutomationScript script)
        {
            _variables.Clear();

            // Script değişkenlerini yükle;
            foreach (var variable in script.Variables)
            {
                _variables[variable.Key] = variable.Value;
            }

            // Sistem değişkenleri;
            _variables["playback_id"] = _currentSession?.SessionId;
            _variables["script_name"] = script.Name;
            _variables["start_time"] = DateTime.Now;
        }

        /// <summary>
        /// Varsayılan komutları kaydet;
        /// </summary>
        private void RegisterDefaultCommands()
        {
            // Dosya işlemleri;
            RegisterCommand("file.copy", new FileCopyCommand());
            RegisterCommand("file.move", new FileMoveCommand());
            RegisterCommand("file.delete", new FileDeleteCommand());
            RegisterCommand("file.read", new FileReadCommand());
            RegisterCommand("file.write", new FileWriteCommand());

            // Sistem işlemleri;
            RegisterCommand("system.execute", new ExecuteCommand());
            RegisterCommand("system.shell", new ShellCommand());
            RegisterCommand("system.wait", new WaitCommand());

            // Değişken işlemleri;
            RegisterCommand("var.set", new SetVariableCommand());
            RegisterCommand("var.get", new GetVariableCommand());
            RegisterCommand("var.clear", new ClearVariableCommand());

            // Matematik işlemleri;
            RegisterCommand("math.calculate", new CalculateCommand());

            _logger.Debug("Varsayılan komutlar kaydedildi");
        }

        /// <summary>
        /// Konfigürasyonu uygula;
        /// </summary>
        private void ApplyConfiguration(AutomationPlayerConfig config)
        {
            // Implement configuration application;
        }

        /// <summary>
        /// Güvenlik kontrolü;
        /// </summary>
        private async Task<bool> CheckSecurityAsync(object resource, SecurityAction action)
        {
            if (_securityManager == null)
                return true;

            try
            {
                return await _securityManager.CheckPermissionAsync(
                    Environment.UserName,
                    resource,
                    action);
            }
            catch (Exception ex)
            {
                _logger.Error($"Güvenlik kontrol hatası: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Script'i XML'den yükle;
        /// </summary>
        private AutomationScript LoadScriptFromXml(string xmlContent)
        {
            // XML parsing logic;
            throw new NotImplementedException();
        }

        /// <summary>
        /// NEDA script'ini yükle;
        /// </summary>
        private AutomationScript LoadNedaScript(string scriptContent)
        {
            // NEDA script parsing logic;
            throw new NotImplementedException();
        }

        /// <summary>
        /// Script'i XML'e dönüştür;
        /// </summary>
        private string ConvertScriptToXml(AutomationScript script)
        {
            // XML conversion logic;
            throw new NotImplementedException();
        }

        /// <summary>
        /// Oynatma temizliği;
        /// </summary>
        private void CleanupPlayback()
        {
            _currentStepIndex = 0;
            _currentScript = null;
            _playbackStopwatch.Reset();

            // Plugin temizliği;
            foreach (var plugin in _plugins)
            {
                plugin.Cleanup();
            }
        }

        #endregion;

        #region Event Triggers;

        protected virtual void OnPlaybackStarted(PlaybackStartedEventArgs e)
        {
            PlaybackStarted?.Invoke(this, e);
        }

        protected virtual void OnPlaybackStopped(PlaybackStoppedEventArgs e)
        {
            PlaybackStopped?.Invoke(this, e);
        }

        protected virtual void OnPlaybackPaused(PlaybackPausedEventArgs e)
        {
            PlaybackPaused?.Invoke(this, e);
        }

        protected virtual void OnPlaybackResumed(PlaybackResumedEventArgs e)
        {
            PlaybackResumed?.Invoke(this, e);
        }

        protected virtual void OnStepExecuted(StepExecutedEventArgs e)
        {
            StepExecuted?.Invoke(this, e);
        }

        protected virtual void OnStepFailed(StepFailedEventArgs e)
        {
            StepFailed?.Invoke(this, e);
        }

        protected virtual void OnPlaybackError(PlaybackErrorEventArgs e)
        {
            PlaybackError?.Invoke(this, e);
        }

        protected virtual void OnPlaybackProgress(PlaybackProgressEventArgs e)
        {
            PlaybackProgress?.Invoke(this, e);
        }

        protected virtual void OnVariableChanged(VariableChangedEventArgs e)
        {
            VariableChanged?.Invoke(this, e);
        }

        protected virtual void OnBreakpointHit(BreakpointHitEventArgs e)
        {
            BreakpointHit?.Invoke(this, e);
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
            if (!_isDisposed)
            {
                if (disposing)
                {
                    Stop();

                    // Plugin'leri temizle;
                    foreach (var plugin in _plugins)
                    {
                        plugin.Dispose();
                    }
                    _plugins.Clear();

                    _logger.Info("AutomationPlayer kaynakları temizlendi");
                }

                _isDisposed = true;
            }
        }

        ~AutomationPlayer()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types;

    /// <summary>
    /// Oynatma durumları;
    /// </summary>
    public enum PlaybackStatus;
    {
        None,
        Playing,
        Paused,
        Completed,
        Stopped,
        Cancelled,
        Error,
        SecurityDenied,
        AlreadyPlaying;
    }

    /// <summary>
    /// Adım türleri;
    /// </summary>
    public enum StepType;
    {
        Command,
        Script,
        Condition,
        Loop,
        Action,
        Wait,
        Variable;
    }

    /// <summary>
    /// Eylem türleri;
    /// </summary>
    public enum ActionType;
    {
        Command,
        UIAction,
        FileOperation,
        SystemAction,
        VariableAction;
    }

    /// <summary>
    /// Güvenlik eylemleri;
    /// </summary>
    public enum SecurityAction;
    {
        Playback,
        Record,
        Edit,
        Delete;
    }

    /// <summary>
    /// Oturum durumları;
    /// </summary>
    public enum PlaybackSessionStatus;
    {
        Starting,
        Playing,
        Paused,
        Completed,
        Stopped,
        Error;
    }

    /// <summary>
    /// AutomationPlayer konfigürasyonu;
    /// </summary>
    public class AutomationPlayerConfig;
    {
        public bool EnableSecurity { get; set; } = true;
        public bool EnableUndoRedo { get; set; } = true;
        public bool EnableBreakpoints { get; set; } = true;
        public bool EnableRecoveryPoints { get; set; } = true;
        public int MaxUndoSteps { get; set; } = 100;
        public int RecoveryPointInterval { get; set; } = 10; // steps;
    }

    /// <summary>
    /// Oynatma seçenekleri;
    /// </summary>
    public class PlaybackOptions;
    {
        public int StepDelay { get; set; } = 0; // ms;
        public bool StopOnError { get; set; } = true;
        public int ErrorTolerance { get; set; } = 0; // max errors before stopping;
        public int MaxRetries { get; set; } = 0;
        public int RetryDelay { get; set; } = 1000; // ms;
        public bool EnableLogging { get; set; } = true;
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
        public Dictionary<string, object> InitialVariables { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Oynatma oturumu;
    /// </summary>
    public class PlaybackSession;
    {
        public string SessionId { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public string ScriptName { get; set; }
        public PlaybackSessionStatus Status { get; set; }
        public PlaybackOptions Options { get; set; }
        public PlaybackResult Result { get; set; }
        public List<Breakpoint> Breakpoints { get; set; } = new List<Breakpoint>();

        public TimeSpan Duration => (EndTime ?? DateTime.Now) - StartTime;
    }

    /// <summary>
    /// Oynatma sonucu;
    /// </summary>
    public class PlaybackResult;
    {
        public string PlaybackId { get; set; }
        public string ScriptName { get; set; }
        public PlaybackStatus Status { get; set; }
        public string Error { get; set; }
        public Exception Exception { get; set; }
        public int TotalSteps { get; set; }
        public int ExecutedSteps { get; set; }
        public int FailedStep { get; set; } = -1;
        public TimeSpan ElapsedTime { get; set; }
        public Dictionary<string, object> FinalVariables { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Oynatma istatistikleri;
    /// </summary>
    public class PlaybackStats;
    {
        public TimeSpan TotalPlaybackTime { get; set; }
        public TimeSpan AverageStepTime { get; set; }
        public int TotalStepsExecuted { get; set; }
        public int SuccessfulSteps { get; set; }
        public int FailedSteps { get; set; }
        public PlaybackSession LastPlaybackSession { get; set; }

        public double SuccessRate => TotalStepsExecuted > 0 ?
            (SuccessfulSteps * 100.0) / TotalStepsExecuted : 0;
    }

    /// <summary>
    /// Otomasyon script'i;
    /// </summary>
    public class AutomationScript;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; } = "1.0";
        public string Description { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string Author { get; set; }
        public List<AutomationStep> Steps { get; set; } = new List<AutomationStep>();
        public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();
        public Dictionary<string, string> Metadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Otomasyon adımı;
    /// </summary>
    public class AutomationStep;
    {
        public string Id { get; set; }
        public StepType StepType { get; set; }
        public string Command { get; set; }
        public string Description { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public List<AutomationStep> ChildSteps { get; set; } = new List<AutomationStep>();
        public string ResultVariable { get; set; }
        public bool SupportsUndo { get; set; } = true;
        public DateTime Timestamp { get; set; } = DateTime.Now;

        public AutomationStep Clone()
        {
            return new AutomationStep;
            {
                Id = this.Id,
                StepType = this.StepType,
                Command = this.Command,
                Description = this.Description,
                Parameters = new Dictionary<string, object>(this.Parameters),
                ChildSteps = this.ChildSteps?.Select(s => s.Clone()).ToList() ?? new List<AutomationStep>(),
                ResultVariable = this.ResultVariable,
                SupportsUndo = this.SupportsUndo,
                Timestamp = this.Timestamp;
            };
        }
    }

    /// <summary>
    /// Breakpoint;
    /// </summary>
    public class Breakpoint;
    {
        public int StepIndex { get; set; }
        public BreakpointCondition Condition { get; set; }
        public bool IsEnabled { get; set; } = true;
        public int HitCount { get; set; }
    }

    /// <summary>
    /// Breakpoint koşulu;
    /// </summary>
    public class BreakpointCondition;
    {
        public string Expression { get; set; }
        public int HitCount { get; set; }
        public bool StopOnHit { get; set; } = true;
    }

    /// <summary>
    /// Oynatma bağlamı;
    /// </summary>
    public class PlaybackContext;
    {
        public string SessionId { get; set; }
        public Dictionary<string, object> Variables { get; set; }
        public int CurrentStep { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Komut bağlamı;
    /// </summary>
    public class CommandContext;
    {
        public AutomationStep Step { get; set; }
        public Dictionary<string, object> Variables { get; set; }
        public Dictionary<string, object> Environment { get; set; }
        public CancellationToken CancellationToken { get; set; }
    }

    /// <summary>
    /// Oynatma eylemi;
    /// </summary>
    public class PlaybackAction;
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public ActionType ActionType { get; set; }
        public AutomationStep Step { get; set; }
        public Dictionary<string, object> StateBefore { get; set; }
        public Dictionary<string, object> StateAfter { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Kayıt noktası;
    /// </summary>
    public class RecoveryPoint;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public DateTime Timestamp { get; set; }
        public Dictionary<string, object> Variables { get; set; }
        public Dictionary<string, object> Environment { get; set; }
        public int CurrentStep { get; set; }
    }

    #region Event Args Classes;

    public class PlaybackStartedEventArgs : EventArgs;
    {
        public PlaybackSession Session { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackStoppedEventArgs : EventArgs;
    {
        public PlaybackSession Session { get; set; }
        public int CurrentStep { get; set; }
        public TimeSpan ElapsedTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackPausedEventArgs : EventArgs;
    {
        public PlaybackSession Session { get; set; }
        public int CurrentStep { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackResumedEventArgs : EventArgs;
    {
        public PlaybackSession Session { get; set; }
        public int CurrentStep { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StepExecutedEventArgs : EventArgs;
    {
        public AutomationStep Step { get; set; }
        public int StepIndex { get; set; }
        public bool Success { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StepFailedEventArgs : EventArgs;
    {
        public AutomationStep Step { get; set; }
        public int StepIndex { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackErrorEventArgs : EventArgs;
    {
        public PlaybackSession Session { get; set; }
        public int StepIndex { get; set; }
        public Exception Exception { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class PlaybackProgressEventArgs : EventArgs;
    {
        public PlaybackSession Session { get; set; }
        public int CurrentStep { get; set; }
        public int TotalSteps { get; set; }
        public int ProgressPercentage { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class VariableChangedEventArgs : EventArgs;
    {
        public string VariableName { get; set; }
        public object OldValue { get; set; }
        public object NewValue { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BreakpointHitEventArgs : EventArgs;
    {
        public int StepIndex { get; set; }
        public AutomationStep Step { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Interfaces;

    /// <summary>
    /// AutomationPlayer interface;
    /// </summary>
    public interface IAutomationPlayer : IDisposable
    {
        bool IsPlaying { get; }
        bool IsPaused { get; }
        PlaybackSession CurrentSession { get; }
        AutomationScript CurrentScript { get; }
        int CurrentStepIndex { get; }
        int TotalSteps { get; }
        TimeSpan ElapsedTime { get; }
        IReadOnlyDictionary<string, object> Variables { get; }
        PerformanceStats PerformanceStats { get; }

        event EventHandler<PlaybackStartedEventArgs> PlaybackStarted;
        event EventHandler<PlaybackStoppedEventArgs> PlaybackStopped;
        event EventHandler<PlaybackPausedEventArgs> PlaybackPaused;
        event EventHandler<PlaybackResumedEventArgs> PlaybackResumed;
        event EventHandler<StepExecutedEventArgs> StepExecuted;
        event EventHandler<StepFailedEventArgs> StepFailed;
        event EventHandler<PlaybackErrorEventArgs> PlaybackError;
        event EventHandler<PlaybackProgressEventArgs> PlaybackProgress;
        event EventHandler<VariableChangedEventArgs> VariableChanged;
        event EventHandler<BreakpointHitEventArgs> BreakpointHit;

        Task<PlaybackResult> PlayMacroAsync(RecordedMacro macro, PlaybackOptions options = null, CancellationToken cancellationToken = default);
        Task<PlaybackResult> PlayScriptAsync(AutomationScript script, PlaybackOptions options = null, CancellationToken cancellationToken = default);
        Task<PlaybackResult> PlayScriptFileAsync(string filePath, PlaybackOptions options = null, CancellationToken cancellationToken = default);
        void Pause();
        void Resume();
        void Stop();
        Task<bool> StepNextAsync();
        Task<bool> GoToStepAsync(int stepIndex);
        void SetVariable(string name, object value);
        object GetVariable(string name);
        bool RegisterCommand(string commandName, IAutomationCommand command);
        bool RegisterPlugin(IAutomationPlugin plugin);
        void AddBreakpoint(int stepIndex, BreakpointCondition condition = null);
        Task<bool> UndoAsync();
        Task<bool> RedoAsync();
        RecoveryPoint CreateRecoveryPoint(string name = null);
        Task<bool> RestoreToRecoveryPointAsync(string recoveryPointId);
        Task<AutomationScript> LoadScriptFromFileAsync(string filePath);
        Task SaveScriptToFileAsync(AutomationScript script, string filePath);
        PlaybackStats GetPlaybackStats();
    }

    /// <summary>
    /// Otomasyon komut interface'i;
    /// </summary>
    public interface IAutomationCommand;
    {
        string Name { get; }
        string Description { get; }
        Task<bool> ExecuteAsync(Dictionary<string, object> parameters, CommandContext context);
        Task<bool> UndoAsync(Dictionary<string, object> parameters, CommandContext context);
    }

    /// <summary>
    /// Otomasyon plugin interface'i;
    /// </summary>
    public interface IAutomationPlugin : IDisposable
    {
        string Name { get; }
        string Version { get; }
        void Initialize(IAutomationPlayer player);
        Task<AutomationScript> PreprocessScriptAsync(AutomationScript script);
        Task<AutomationStep> PreprocessStepAsync(AutomationStep step);
        Task<PlaybackResult> PostprocessScriptAsync(AutomationScript script, PlaybackResult result);
        void Cleanup();
    }

    /// <summary>
    /// Macro kaydedici interface'i;
    /// </summary>
    public interface IMacroRecorder;
    {
        // Macro recording interface;
    }

    /// <summary>
    /// Script motoru interface'i;
    /// </summary>
    public interface IScriptEngine;
    {
        Task<ScriptResult> ExecuteAsync(string script, Dictionary<string, object> variables, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Kayıtlı makro;
    /// </summary>
    public class RecordedMacro;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Version { get; set; }
        public DateTime Created { get; set; }
        public DateTime Modified { get; set; }
        public string Author { get; set; }
        public List<RecordedAction> Actions { get; set; } = new List<RecordedAction>();
    }

    /// <summary>
    /// Kayıtlı aksiyon;
    /// </summary>
    public class RecordedAction;
    {
        public string ActionType { get; set; }
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Script sonucu;
    /// </summary>
    public class ScriptResult;
    {
        public bool Success { get; set; }
        public object Result { get; set; }
        public string Error { get; set; }
        public TimeSpan ExecutionTime { get; set; }
    }

    #endregion;

    #region Exception Classes;

    public class AutomationPlaybackException : Exception
    {
        public AutomationPlaybackException(string message) : base(message) { }
        public AutomationPlaybackException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AutomationScriptException : Exception
    {
        public AutomationScriptException(string message) : base(message) { }
        public AutomationScriptException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class AutomationCommandException : Exception
    {
        public AutomationCommandException(string message) : base(message) { }
        public AutomationCommandException(string message, Exception innerException) : base(message, innerException) { }
    }

    #endregion;

    #region Utility Classes;

    /// <summary>
    /// Performans monitörü;
    /// </summary>
    public class PerformanceMonitor;
    {
        private readonly Stopwatch _monitoringStopwatch = new Stopwatch();
        private readonly List<TimeSpan> _stepTimes = new List<TimeSpan>();
        private int _successfulSteps;
        private int _failedSteps;

        public int TotalStepsExecuted => _successfulSteps + _failedSteps;
        public int SuccessfulSteps => _successfulSteps;
        public int FailedSteps => _failedSteps;

        public void StartMonitoring()
        {
            _monitoringStopwatch.Restart();
            _stepTimes.Clear();
            _successfulSteps = 0;
            _failedSteps = 0;
        }

        public void StopMonitoring()
        {
            _monitoringStopwatch.Stop();
        }

        public void RecordStepTime(TimeSpan stepTime)
        {
            _stepTimes.Add(stepTime);
        }

        public void RecordSuccessfulStep()
        {
            _successfulSteps++;
        }

        public void RecordFailedStep()
        {
            _failedSteps++;
        }

        public TimeSpan GetTotalPlaybackTime()
        {
            return _monitoringStopwatch.Elapsed;
        }

        public TimeSpan GetAverageStepTime()
        {
            if (_stepTimes.Count == 0)
                return TimeSpan.Zero;

            var totalTicks = _stepTimes.Sum(t => t.Ticks);
            return TimeSpan.FromTicks(totalTicks / _stepTimes.Count);
        }

        public PerformanceStats GetStats()
        {
            return new PerformanceStats;
            {
                TotalPlaybackTime = GetTotalPlaybackTime(),
                AverageStepTime = GetAverageStepTime(),
                TotalSteps = TotalStepsExecuted,
                SuccessfulSteps = _successfulSteps,
                FailedSteps = _failedSteps,
                StepTimes = _stepTimes.ToArray()
            };
        }
    }

    /// <summary>
    /// Performans istatistikleri;
    /// </summary>
    public class PerformanceStats;
    {
        public TimeSpan TotalPlaybackTime { get; set; }
        public TimeSpan AverageStepTime { get; set; }
        public int TotalSteps { get; set; }
        public int SuccessfulSteps { get; set; }
        public int FailedSteps { get; set; }
        public TimeSpan[] StepTimes { get; set; }

        public double SuccessRate => TotalSteps > 0 ? (SuccessfulSteps * 100.0) / TotalSteps : 0;
    }

    /// <summary>
    /// Koşul değerlendirici;
    /// </summary>
    public class ConditionEvaluator;
    {
        public bool Evaluate(string condition, Dictionary<string, object> variables)
        {
            if (string.IsNullOrEmpty(condition))
                return true;

            try
            {
                // Basit koşul değerlendirme;
                // Gerçek implementasyonda daha gelişmiş bir parser kullanılmalı;
                var tokens = condition.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

                if (tokens.Length == 3)
                {
                    var left = GetValue(tokens[0], variables);
                    var op = tokens[1];
                    var right = GetValue(tokens[2], variables);

                    return EvaluateComparison(left, op, right);
                }

                // Boolean değişken kontrolü;
                if (variables.TryGetValue(condition, out var value) && value is bool boolValue)
                {
                    return boolValue;
                }

                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private object GetValue(string token, Dictionary<string, object> variables)
        {
            if (variables.TryGetValue(token.TrimStart('$', '{').TrimEnd('}'), out var value))
            {
                return value;
            }

            // Sabit değer;
            if (int.TryParse(token, out var intValue))
                return intValue;

            if (bool.TryParse(token, out var boolValue))
                return boolValue;

            if (double.TryParse(token, out var doubleValue))
                return doubleValue;

            return token;
        }

        private bool EvaluateComparison(object left, string op, object right)
        {
            try
            {
                switch (op)
                {
                    case "==":
                        return object.Equals(left, right);
                    case "!=":
                        return !object.Equals(left, right);
                    case ">":
                        return Convert.ToDouble(left) > Convert.ToDouble(right);
                    case "<":
                        return Convert.ToDouble(left) < Convert.ToDouble(right);
                    case ">=":
                        return Convert.ToDouble(left) >= Convert.ToDouble(right);
                    case "<=":
                        return Convert.ToDouble(left) <= Convert.ToDouble(right);
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    #endregion;

    #region Default Command Implementations;

    // Örnek komut implementasyonları;
    public class FileCopyCommand : IAutomationCommand;
    {
        public string Name => "file.copy";
        public string Description => "Dosya kopyalar";

        public async Task<bool> ExecuteAsync(Dictionary<string, object> parameters, CommandContext context)
        {
            var source = parameters.GetValueOrDefault("source", "")?.ToString();
            var destination = parameters.GetValueOrDefault("destination", "")?.ToString();

            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(destination))
                return false;

            try
            {
                File.Copy(source, destination, true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> UndoAsync(Dictionary<string, object> parameters, CommandContext context)
        {
            var destination = parameters.GetValueOrDefault("destination", "")?.ToString();

            try
            {
                if (File.Exists(destination))
                {
                    File.Delete(destination);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // Diğer komut implementasyonları...

    #endregion;

    #endregion;
}
