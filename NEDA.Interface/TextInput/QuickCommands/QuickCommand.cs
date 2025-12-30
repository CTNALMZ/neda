using NEDA.Core.Engine;
using NEDA.Core.Logging;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Security;
using NEDA.Interface.TextInput.QuickCommands.Contracts;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace NEDA.Interface.TextInput.QuickCommands;
{
    /// <summary>
    /// Hızlı komut tanımını temsil eden sınıf;
    /// </summary>
    public class QuickCommand;
    {
        /// <summary>
        /// Komut benzersiz tanımlayıcısı;
        /// </summary>
        [JsonPropertyName("id")]
        public string Id { get; set; }

        /// <summary>
        /// Komut adı;
        /// </summary>
        [JsonPropertyName("name")]
        public string Name { get; set; }

        /// <summary>
        /// Komut açıklaması;
        /// </summary>
        [JsonPropertyName("description")]
        public string Description { get; set; }

        /// <summary>
        /// Kısayol tetikleyicisi;
        /// </summary>
        [JsonPropertyName("trigger")]
        public string Trigger { get; set; }

        /// <summary>
        /// Komut kategorisi;
        /// </summary>
        [JsonPropertyName("category")]
        public string Category { get; set; }

        /// <summary>
        /// Yürütülecek komut dizisi;
        /// </summary>
        [JsonPropertyName("commandSequence")]
        public List<CommandAction> CommandSequence { get; set; } = new List<CommandAction>();

        /// <summary>
        /// Parametre tanımları;
        /// </summary>
        [JsonPropertyName("parameters")]
        public List<CommandParameter> Parameters { get; set; } = new List<CommandParameter>();

        /// <summary>
        /// Yetkilendirme gereksinimleri;
        /// </summary>
        [JsonPropertyName("permissions")]
        public List<string> RequiredPermissions { get; set; } = new List<string>();

        /// <summary>
        /// Komut çalışma zamanı değişkenleri;
        /// </summary>
        [JsonPropertyName("runtimeVariables")]
        public Dictionary<string, string> RuntimeVariables { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// Önbellekleme politikası;
        /// </summary>
        [JsonPropertyName("cachePolicy")]
        public CachePolicy CachePolicy { get; set; } = CachePolicy.Default;

        /// <summary>
        /// Yürütme bağlamı;
        /// </summary>
        [JsonPropertyName("executionContext")]
        public ExecutionContext ExecutionContext { get; set; } = ExecutionContext.User;

        /// <summary>
        /// Komut etkinlik durumu;
        /// </summary>
        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Son yürütülme zamanı;
        /// </summary>
        [JsonPropertyName("lastExecuted")]
        public DateTime? LastExecuted { get; set; }

        /// <summary>
        /// Yürütme sayacı;
        /// </summary>
        [JsonPropertyName("executionCount")]
        public int ExecutionCount { get; set; }

        /// <summary>
        /// Komut versiyonu;
        /// </summary>
        [JsonPropertyName("version")]
        public string Version { get; set; } = "1.0.0";

        /// <summary>
        /// Komut öncelik seviyesi;
        /// </summary>
        [JsonPropertyName("priority")]
        public CommandPriority Priority { get; set; } = CommandPriority.Normal;

        /// <summary>
        /// Komutun oluşturulma zamanı;
        /// </summary>
        [JsonPropertyName("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Komutu oluşturan kullanıcı;
        /// </summary>
        [JsonPropertyName("createdBy")]
        public string CreatedBy { get; set; }

        /// <summary>
        /// Son güncellenme zamanı;
        /// </summary>
        [JsonPropertyName("updatedAt")]
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Komut etiketleri;
        /// </summary>
        [JsonPropertyName("tags")]
        public List<string> Tags { get; set; } = new List<string>();

        /// <summary>
        /// Bağımlılıklar;
        /// </summary>
        [JsonPropertyName("dependencies")]
        public List<string> Dependencies { get; set; } = new List<string>();

        /// <summary>
        /// Geçerli komutu doğrular;
        /// </summary>
        public ValidationResult Validate()
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(Id))
                errors.Add("Id is required");

            if (string.IsNullOrWhiteSpace(Name))
                errors.Add("Name is required");

            if (string.IsNullOrWhiteSpace(Trigger))
                errors.Add("Trigger is required");

            if (CommandSequence == null || CommandSequence.Count == 0)
                errors.Add("At least one command action is required");

            // Tetikleyici formatını doğrula;
            if (!IsValidTrigger(Trigger))
                errors.Add($"Invalid trigger format: {Trigger}");

            // Parametre isimlerini doğrula;
            foreach (var param in Parameters)
            {
                if (string.IsNullOrWhiteSpace(param.Name))
                    errors.Add($"Parameter name cannot be empty");

                if (param.Name.Contains(" "))
                    errors.Add($"Parameter name '{param.Name}' cannot contain spaces");
            }

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        /// <summary>
        /// Tetikleyici formatını doğrular;
        /// </summary>
        private bool IsValidTrigger(string trigger)
        {
            if (string.IsNullOrWhiteSpace(trigger))
                return false;

            // Tetikleyici alfanumerik ve bazı özel karakterler içerebilir;
            return trigger.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || c == ' ');
        }

        /// <summary>
        /// Komutu kopyalar;
        /// </summary>
        public QuickCommand Clone()
        {
            return new QuickCommand;
            {
                Id = Guid.NewGuid().ToString(),
                Name = $"{Name} (Copy)",
                Description = Description,
                Trigger = $"{Trigger}_copy",
                Category = Category,
                CommandSequence = CommandSequence.Select(c => c.Clone()).ToList(),
                Parameters = Parameters.Select(p => p.Clone()).ToList(),
                RequiredPermissions = new List<string>(RequiredPermissions),
                RuntimeVariables = new Dictionary<string, string>(RuntimeVariables),
                CachePolicy = CachePolicy,
                ExecutionContext = ExecutionContext,
                IsEnabled = IsEnabled,
                Version = Version,
                Priority = Priority,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = CreatedBy,
                UpdatedAt = DateTime.UtcNow,
                Tags = new List<string>(Tags),
                Dependencies = new List<string>(Dependencies)
            };
        }

        /// <summary>
        /// Komut parametre değerlerini günceller;
        /// </summary>
        public void UpdateParameterValues(Dictionary<string, object> parameterValues)
        {
            if (parameterValues == null)
                return;

            foreach (var param in Parameters)
            {
                if (parameterValues.TryGetValue(param.Name, out var value))
                {
                    param.Value = value?.ToString();
                }
            }
        }

        /// <summary>
        /// Komut için özet bilgi oluşturur;
        /// </summary>
        public string GetSummary()
        {
            return $"{Name}: {Description} (Trigger: {Trigger}, Actions: {CommandSequence.Count})";
        }
    }

    /// <summary>
    /// Komut eylemini temsil eden sınıf;
    /// </summary>
    public class CommandAction;
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [JsonPropertyName("actionType")]
        public ActionType ActionType { get; set; }

        [JsonPropertyName("target")]
        public string Target { get; set; }

        [JsonPropertyName("command")]
        public string Command { get; set; }

        [JsonPropertyName("parameters")]
        public Dictionary<string, string> Parameters { get; set; } = new Dictionary<string, string>();

        [JsonPropertyName("delayBefore")]
        public int DelayBeforeMs { get; set; }

        [JsonPropertyName("delayAfter")]
        public int DelayAfterMs { get; set; }

        [JsonPropertyName("retryCount")]
        public int RetryCount { get; set; }

        [JsonPropertyName("retryDelay")]
        public int RetryDelayMs { get; set; }

        [JsonPropertyName("condition")]
        public string Condition { get; set; }

        [JsonPropertyName("timeout")]
        public int TimeoutMs { get; set; } = 30000;

        [JsonPropertyName("isAsync")]
        public bool IsAsync { get; set; }

        [JsonPropertyName("expectedResult")]
        public string ExpectedResult { get; set; }

        public CommandAction Clone()
        {
            return new CommandAction;
            {
                Id = Guid.NewGuid().ToString(),
                ActionType = ActionType,
                Target = Target,
                Command = Command,
                Parameters = new Dictionary<string, string>(Parameters),
                DelayBeforeMs = DelayBeforeMs,
                DelayAfterMs = DelayAfterMs,
                RetryCount = RetryCount,
                RetryDelayMs = RetryDelayMs,
                Condition = Condition,
                TimeoutMs = TimeoutMs,
                IsAsync = IsAsync,
                ExpectedResult = ExpectedResult;
            };
        }
    }

    /// <summary>
    /// Komut parametresini temsil eden sınıf;
    /// </summary>
    public class CommandParameter;
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("description")]
        public string Description { get; set; }

        [JsonPropertyName("type")]
        public ParameterType Type { get; set; } = ParameterType.String;

        [JsonPropertyName("defaultValue")]
        public string DefaultValue { get; set; }

        [JsonPropertyName("value")]
        public string Value { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("validationPattern")]
        public string ValidationPattern { get; set; }

        [JsonPropertyName("options")]
        public List<string> Options { get; set; } = new List<string>();

        [JsonPropertyName("minValue")]
        public double? MinValue { get; set; }

        [JsonPropertyName("maxValue")]
        public double? MaxValue { get; set; }

        public CommandParameter Clone()
        {
            return new CommandParameter;
            {
                Name = Name,
                Description = Description,
                Type = Type,
                DefaultValue = DefaultValue,
                Value = Value,
                IsRequired = IsRequired,
                ValidationPattern = ValidationPattern,
                Options = new List<string>(Options),
                MinValue = MinValue,
                MaxValue = MaxValue;
            };
        }

        /// <summary>
        /// Parametre değerini doğrular;
        /// </summary>
        public ValidationResult ValidateValue(string value)
        {
            var errors = new List<string>();

            if (IsRequired && string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"Parameter '{Name}' is required");
                return new ValidationResult { IsValid = false, Errors = errors };
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                // Tip doğrulama;
                if (!IsValidType(value))
                    errors.Add($"Invalid value type for parameter '{Name}'. Expected: {Type}");

                // Validasyon pattern'i;
                if (!string.IsNullOrWhiteSpace(ValidationPattern))
                {
                    try
                    {
                        var regex = new System.Text.RegularExpressions.Regex(ValidationPattern);
                        if (!regex.IsMatch(value))
                            errors.Add($"Value does not match validation pattern for parameter '{Name}'");
                    }
                    catch
                    {
                        errors.Add($"Invalid validation pattern for parameter '{Name}'");
                    }
                }

                // Min/Max değer kontrolü;
                if (Type == ParameterType.Number && double.TryParse(value, out double numericValue))
                {
                    if (MinValue.HasValue && numericValue < MinValue.Value)
                        errors.Add($"Value must be at least {MinValue.Value} for parameter '{Name}'");

                    if (MaxValue.HasValue && numericValue > MaxValue.Value)
                        errors.Add($"Value must be at most {MaxValue.Value} for parameter '{Name}'");
                }

                // Seçenekler kontrolü;
                if (Options != null && Options.Count > 0 && !Options.Contains(value))
                    errors.Add($"Value must be one of: {string.Join(", ", Options)} for parameter '{Name}'");
            }

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                Errors = errors;
            };
        }

        private bool IsValidType(string value)
        {
            switch (Type)
            {
                case ParameterType.String:
                    return true;
                case ParameterType.Number:
                    return double.TryParse(value, out _);
                case ParameterType.Boolean:
                    return bool.TryParse(value, out _);
                case ParameterType.Integer:
                    return int.TryParse(value, out _);
                case ParameterType.DateTime:
                    return DateTime.TryParse(value, out _);
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Hızlı komut servisi implementasyonu;
    /// </summary>
    public class QuickCommandService : IQuickCommandService;
    {
        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IPermissionValidator _permissionValidator;
        private readonly IEventBus _eventBus;
        private readonly QuickCommandConfiguration _configuration;
        private readonly ConcurrentDictionary<string, QuickCommand> _commandCache;
        private readonly ConcurrentDictionary<string, DateTime> _executionLocks;
        private readonly SemaphoreSlim _cacheLock = new SemaphoreSlim(1, 1);
        private DateTime _lastCacheCleanup = DateTime.UtcNow;

        public QuickCommandService(
            ILogger logger,
            IErrorHandler errorHandler,
            IPermissionValidator permissionValidator,
            IEventBus eventBus,
            QuickCommandConfiguration configuration = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _permissionValidator = permissionValidator ?? throw new ArgumentNullException(nameof(permissionValidator));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _configuration = configuration ?? QuickCommandConfiguration.Default;
            _commandCache = new ConcurrentDictionary<string, QuickCommand>();
            _executionLocks = new ConcurrentDictionary<string, DateTime>();
        }

        /// <summary>
        /// Hızlı komutu kaydeder;
        /// </summary>
        public async Task<CommandOperationResult> RegisterCommandAsync(
            QuickCommand command,
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            try
            {
                // Komutu doğrula;
                var validation = command.Validate();
                if (!validation.IsValid)
                {
                    return CommandOperationResult.Failure(
                        $"Command validation failed: {string.Join(", ", validation.Errors)}");
                }

                // Yetki kontrolü;
                if (!await HasPermissionToRegisterAsync(userId, command))
                {
                    return CommandOperationResult.Failure(
                        $"User '{userId}' does not have permission to register this command");
                }

                await _cacheLock.WaitAsync(cancellationToken);
                try
                {
                    // Çakışma kontrolü;
                    if (_commandCache.ContainsKey(command.Id))
                    {
                        return CommandOperationResult.Failure(
                            $"Command with id '{command.Id}' already exists");
                    }

                    if (_commandCache.Values.Any(c => c.Trigger == command.Trigger && c.IsEnabled))
                    {
                        return CommandOperationResult.Failure(
                            $"Command with trigger '{command.Trigger}' already exists");
                    }

                    // Komutu kaydet;
                    command.CreatedBy = userId;
                    command.UpdatedAt = DateTime.UtcNow;
                    _commandCache[command.Id] = command;

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new QuickCommandRegisteredEvent;
                    {
                        CommandId = command.Id,
                        CommandName = command.Name,
                        UserId = userId,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Quick command registered: {command.Name} (ID: {command.Id})");

                    return CommandOperationResult.Success(command.Id);
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "QuickCommandService",
                    Operation = "RegisterCommand",
                    UserId = userId,
                    AdditionalData = new { CommandName = command?.Name }
                });

                return CommandOperationResult.Failure($"Failed to register command: {ex.Message}");
            }
        }

        /// <summary>
        /// Hızlı komutu günceller;
        /// </summary>
        public async Task<CommandOperationResult> UpdateCommandAsync(
            string commandId,
            QuickCommand updatedCommand,
            string userId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                throw new ArgumentException("Command ID cannot be empty", nameof(commandId));

            if (updatedCommand == null)
                throw new ArgumentNullException(nameof(updatedCommand));

            try
            {
                // Komutu doğrula;
                var validation = updatedCommand.Validate();
                if (!validation.IsValid)
                {
                    return CommandOperationResult.Failure(
                        $"Command validation failed: {string.Join(", ", validation.Errors)}");
                }

                await _cacheLock.WaitAsync(cancellationToken);
                try
                {
                    // Komutun varlığını kontrol et;
                    if (!_commandCache.TryGetValue(commandId, out var existingCommand))
                    {
                        return CommandOperationResult.Failure(
                            $"Command with id '{commandId}' not found");
                    }

                    // Yetki kontrolü;
                    if (!await HasPermissionToModifyAsync(userId, existingCommand))
                    {
                        return CommandOperationResult.Failure(
                            $"User '{userId}' does not have permission to modify this command");
                    }

                    // Tetikleyici çakışmasını kontrol et;
                    if (existingCommand.Trigger != updatedCommand.Trigger &&
                        _commandCache.Values.Any(c => c.Trigger == updatedCommand.Trigger && c.Id != commandId))
                    {
                        return CommandOperationResult.Failure(
                            $"Another command with trigger '{updatedCommand.Trigger}' already exists");
                    }

                    // Komutu güncelle;
                    updatedCommand.Id = commandId;
                    updatedCommand.CreatedAt = existingCommand.CreatedAt;
                    updatedCommand.CreatedBy = existingCommand.CreatedBy;
                    updatedCommand.UpdatedAt = DateTime.UtcNow;
                    updatedCommand.LastExecuted = existingCommand.LastExecuted;
                    updatedCommand.ExecutionCount = existingCommand.ExecutionCount;

                    _commandCache[commandId] = updatedCommand;

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new QuickCommandUpdatedEvent;
                    {
                        CommandId = commandId,
                        CommandName = updatedCommand.Name,
                        UserId = userId,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Quick command updated: {updatedCommand.Name} (ID: {commandId})");

                    return CommandOperationResult.Success(commandId);
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "QuickCommandService",
                    Operation = "UpdateCommand",
                    UserId = userId,
                    AdditionalData = new { CommandId = commandId }
                });

                return CommandOperationResult.Failure($"Failed to update command: {ex.Message}");
            }
        }

        /// <summary>
        /// Hızlı komutu yürütür;
        /// </summary>
        public async Task<CommandExecutionResult> ExecuteCommandAsync(
            string trigger,
            Dictionary<string, object> parameters = null,
            string userId = null,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(trigger))
                throw new ArgumentException("Trigger cannot be empty", nameof(trigger));

            var executionId = Guid.NewGuid().ToString();
            var startTime = DateTime.UtcNow;

            try
            {
                // Komutu bul;
                var command = await FindCommandByTriggerAsync(trigger, cancellationToken);
                if (command == null)
                {
                    return CommandExecutionResult.NotFound($"No command found with trigger: {trigger}");
                }

                if (!command.IsEnabled)
                {
                    return CommandExecutionResult.Failure($"Command '{command.Name}' is disabled");
                }

                // Yetki kontrolü;
                if (!await HasPermissionToExecuteAsync(userId, command))
                {
                    return CommandExecutionResult.Failure(
                        $"User '{userId}' does not have permission to execute this command");
                }

                // Yürütme kilidi;
                if (!await AcquireExecutionLockAsync(command.Id))
                {
                    return CommandExecutionResult.Failure(
                        $"Command '{command.Name}' is already being executed");
                }

                try
                {
                    _logger.LogInformation($"Executing quick command: {command.Name} (ID: {command.Id})");

                    // Parametreleri işle;
                    var processedParameters = await ProcessParametersAsync(
                        command, parameters, context, cancellationToken);

                    // Komut dizisini yürüt;
                    var executionResults = new List<ActionExecutionResult>();
                    var overallSuccess = true;
                    var stopExecution = false;

                    foreach (var action in command.CommandSequence)
                    {
                        if (stopExecution)
                            break;

                        // Koşul kontrolü;
                        if (!string.IsNullOrWhiteSpace(action.Condition))
                        {
                            if (!await EvaluateConditionAsync(action.Condition, processedParameters, cancellationToken))
                            {
                                _logger.LogDebug($"Skipping action {action.Id} due to condition");
                                continue;
                            }
                        }

                        // Gecikme;
                        if (action.DelayBeforeMs > 0)
                        {
                            await Task.Delay(action.DelayBeforeMs, cancellationToken);
                        }

                        // Eylemi yürüt;
                        var actionResult = await ExecuteActionAsync(
                            action,
                            processedParameters,
                            command.ExecutionContext,
                            cancellationToken);

                        executionResults.Add(actionResult);

                        if (!actionResult.IsSuccess)
                        {
                            overallSuccess = false;

                            // Yeniden deneme;
                            if (action.RetryCount > 0)
                            {
                                for (int i = 0; i < action.RetryCount; i++)
                                {
                                    await Task.Delay(action.RetryDelayMs, cancellationToken);

                                    var retryResult = await ExecuteActionAsync(
                                        action,
                                        processedParameters,
                                        command.ExecutionContext,
                                        cancellationToken);

                                    if (retryResult.IsSuccess)
                                    {
                                        overallSuccess = true;
                                        break;
                                    }
                                }
                            }

                            // Başarısız eylem için devam etme kararı;
                            if (actionResult.StopOnFailure)
                                stopExecution = true;
                        }

                        // Sonrası gecikme;
                        if (action.DelayAfterMs > 0)
                        {
                            await Task.Delay(action.DelayAfterMs, cancellationToken);
                        }
                    }

                    // Komut istatistiklerini güncelle;
                    await UpdateCommandStatisticsAsync(command.Id, overallSuccess, cancellationToken);

                    // Sonuç oluştur;
                    var result = BuildExecutionResult(
                        command,
                        executionId,
                        startTime,
                        overallSuccess,
                        executionResults,
                        processedParameters);

                    // Olay yayınla;
                    await _eventBus.PublishAsync(new QuickCommandExecutedEvent;
                    {
                        CommandId = command.Id,
                        CommandName = command.Name,
                        ExecutionId = executionId,
                        UserId = userId,
                        Success = overallSuccess,
                        ExecutionTime = result.ExecutionTime,
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Quick command execution completed: {command.Name} " +
                                          $"(Success: {overallSuccess}, Time: {result.ExecutionTime}ms)");

                    return result;
                }
                finally
                {
                    ReleaseExecutionLock(command.Id);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Command execution cancelled: {trigger}");
                return CommandExecutionResult.Cancelled(executionId);
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "QuickCommandService",
                    Operation = "ExecuteCommand",
                    UserId = userId,
                    AdditionalData = new { Trigger = trigger, ExecutionId = executionId }
                });

                return CommandExecutionResult.Failure(
                    $"Command execution failed: {ex.Message}", executionId);
            }
        }

        /// <summary>
        /// Tetikleyiciye göre komut bulur;
        /// </summary>
        public async Task<QuickCommand> FindCommandByTriggerAsync(
            string trigger,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(trigger))
                return null;

            await PerformCacheMaintenanceAsync();

            var command = _commandCache.Values.FirstOrDefault(c =>
                c.Trigger.Equals(trigger, StringComparison.OrdinalIgnoreCase) &&
                c.IsEnabled);

            // Önbellek politikasına göre kontrol;
            if (command != null && command.CachePolicy.Enabled)
            {
                var cacheAge = DateTime.UtcNow - (command.LastExecuted ?? command.UpdatedAt);
                if (cacheAge > command.CachePolicy.Duration)
                {
                    // Önbellek süresi dolmuş, komutu devre dışı bırak;
                    if (command.CachePolicy.InvalidateOnExpiry)
                    {
                        _logger.LogDebug($"Command cache expired: {command.Name}");
                        return null;
                    }
                }
            }

            return command;
        }

        /// <summary>
        /// Kategoriye göre komutları listeler;
        /// </summary>
        public async Task<IEnumerable<QuickCommand>> GetCommandsByCategoryAsync(
            string category,
            bool includeDisabled = false,
            CancellationToken cancellationToken = default)
        {
            await PerformCacheMaintenanceAsync();

            return _commandCache.Values;
                .Where(c => (includeDisabled || c.IsEnabled) &&
                           (string.IsNullOrEmpty(category) || c.Category == category))
                .OrderBy(c => c.Priority)
                .ThenBy(c => c.Name)
                .ToList();
        }

        /// <summary>
        /// Komutu devre dışı bırakır;
        /// </summary>
        public async Task<CommandOperationResult> DisableCommandAsync(
            string commandId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            return await SetCommandEnabledStatusAsync(commandId, false, userId, cancellationToken);
        }

        /// <summary>
        /// Komutu etkinleştirir;
        /// </summary>
        public async Task<CommandOperationResult> EnableCommandAsync(
            string commandId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            return await SetCommandEnabledStatusAsync(commandId, true, userId, cancellationToken);
        }

        /// <summary>
        /// Komutu siler;
        /// </summary>
        public async Task<CommandOperationResult> DeleteCommandAsync(
            string commandId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await _cacheLock.WaitAsync(cancellationToken);
                try
                {
                    if (!_commandCache.TryGetValue(commandId, out var command))
                    {
                        return CommandOperationResult.Failure($"Command with id '{commandId}' not found");
                    }

                    // Yetki kontrolü;
                    if (!await HasPermissionToDeleteAsync(userId, command))
                    {
                        return CommandOperationResult.Failure(
                            $"User '{userId}' does not have permission to delete this command");
                    }

                    if (_commandCache.TryRemove(commandId, out _))
                    {
                        // Olay yayınla;
                        await _eventBus.PublishAsync(new QuickCommandDeletedEvent;
                        {
                            CommandId = commandId,
                            CommandName = command.Name,
                            UserId = userId,
                            Timestamp = DateTime.UtcNow;
                        }, cancellationToken);

                        _logger.LogInformation($"Quick command deleted: {command.Name} (ID: {commandId})");

                        return CommandOperationResult.Success(commandId);
                    }

                    return CommandOperationResult.Failure($"Failed to delete command '{commandId}'");
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "QuickCommandService",
                    Operation = "DeleteCommand",
                    UserId = userId,
                    AdditionalData = new { CommandId = commandId }
                });

                return CommandOperationResult.Failure($"Failed to delete command: {ex.Message}");
            }
        }

        /// <summary>
        /// Komut bağımlılıklarını kontrol eder;
        /// </summary>
        public async Task<DependencyCheckResult> CheckDependenciesAsync(
            string commandId,
            CancellationToken cancellationToken = default)
        {
            if (!_commandCache.TryGetValue(commandId, out var command))
            {
                return DependencyCheckResult.NotFound($"Command with id '{commandId}' not found");
            }

            var missingDependencies = new List<string>();
            var availableDependencies = new List<string>();

            foreach (var dependency in command.Dependencies)
            {
                var dependentCommand = _commandCache.Values.FirstOrDefault(c =>
                    c.Id == dependency || c.Name == dependency);

                if (dependentCommand == null || !dependentCommand.IsEnabled)
                {
                    missingDependencies.Add(dependency);
                }
                else;
                {
                    availableDependencies.Add(dependency);
                }
            }

            return new DependencyCheckResult;
            {
                CommandId = commandId,
                HasAllDependencies = missingDependencies.Count == 0,
                MissingDependencies = missingDependencies,
                AvailableDependencies = availableDependencies;
            };
        }

        /// <summary>
        /// Komut istatistiklerini alır;
        /// </summary>
        public async Task<CommandStatistics> GetCommandStatisticsAsync(
            string commandId,
            CancellationToken cancellationToken = default)
        {
            if (!_commandCache.TryGetValue(commandId, out var command))
            {
                throw new KeyNotFoundException($"Command with id '{commandId}' not found");
            }

            var similarCommands = _commandCache.Values;
                .Where(c => c.Category == command.Category && c.Id != commandId)
                .ToList();

            return new CommandStatistics;
            {
                CommandId = commandId,
                ExecutionCount = command.ExecutionCount,
                LastExecuted = command.LastExecuted,
                AverageExecutionTime = await CalculateAverageExecutionTimeAsync(commandId, cancellationToken),
                SuccessRate = await CalculateSuccessRateAsync(commandId, cancellationToken),
                CategoryRank = similarCommands;
                    .OrderByDescending(c => c.ExecutionCount)
                    .ToList()
                    .IndexOf(command) + 1,
                TotalCategoryCommands = similarCommands.Count + 1;
            };
        }

        #region Private Methods;

        private async Task<bool> HasPermissionToRegisterAsync(string userId, QuickCommand command)
        {
            if (string.IsNullOrWhiteSpace(userId) || command == null)
                return false;

            // Temel yetki kontrolü;
            var hasBasePermission = await _permissionValidator.HasPermissionAsync(
                userId, "QuickCommand.Register");

            // Komut özel yetki kontrolü;
            if (command.RequiredPermissions.Any())
            {
                foreach (var permission in command.RequiredPermissions)
                {
                    if (!await _permissionValidator.HasPermissionAsync(userId, permission))
                        return false;
                }
            }

            return hasBasePermission;
        }

        private async Task<bool> HasPermissionToModifyAsync(string userId, QuickCommand command)
        {
            if (string.IsNullOrWhiteSpace(userId) || command == null)
                return false;

            // Komut sahibi kontrolü;
            if (command.CreatedBy == userId)
                return true;

            // Admin yetkisi kontrolü;
            return await _permissionValidator.HasPermissionAsync(
                userId, "QuickCommand.Admin");
        }

        private async Task<bool> HasPermissionToExecuteAsync(string userId, QuickCommand command)
        {
            if (command == null)
                return false;

            // Anonim yürütme kontrolü;
            if (string.IsNullOrWhiteSpace(userId))
                return command.ExecutionContext == ExecutionContext.System;

            // Yetki kontrolü;
            if (command.RequiredPermissions.Any())
            {
                foreach (var permission in command.RequiredPermissions)
                {
                    if (!await _permissionValidator.HasPermissionAsync(userId, permission))
                        return false;
                }
            }

            return true;
        }

        private async Task<bool> HasPermissionToDeleteAsync(string userId, QuickCommand command)
        {
            return await HasPermissionToModifyAsync(userId, command);
        }

        private async Task<bool> AcquireExecutionLockAsync(string commandId)
        {
            var now = DateTime.UtcNow;
            var lockTimeout = TimeSpan.FromSeconds(_configuration.ExecutionLockTimeoutSeconds);

            // Eski kilitleri temizle;
            var expiredLocks = _executionLocks.Where(kvp =>
                now - kvp.Value > lockTimeout).ToList();

            foreach (var expired in expiredLocks)
            {
                _executionLocks.TryRemove(expired.Key, out _);
            }

            // Yeni kilit ekle;
            return _executionLocks.TryAdd(commandId, now);
        }

        private void ReleaseExecutionLock(string commandId)
        {
            _executionLocks.TryRemove(commandId, out _);
        }

        private async Task<Dictionary<string, object>> ProcessParametersAsync(
            QuickCommand command,
            Dictionary<string, object> providedParameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            var processed = new Dictionary<string, object>();

            foreach (var param in command.Parameters)
            {
                object value = null;

                // Sağlanan parametreden al;
                if (providedParameters != null &&
                    providedParameters.TryGetValue(param.Name, out var providedValue))
                {
                    value = providedValue;
                }
                // Varsayılan değer;
                else if (!string.IsNullOrWhiteSpace(param.DefaultValue))
                {
                    value = param.DefaultValue;
                }

                // Değeri doğrula;
                var validation = param.ValidateValue(value?.ToString());
                if (!validation.IsValid)
                {
                    throw new InvalidOperationException(
                        $"Parameter validation failed for '{param.Name}': {string.Join(", ", validation.Errors)}");
                }

                processed[param.Name] = value;
            }

            // Bağlam değişkenlerini ekle;
            if (context != null)
            {
                foreach (var variable in context.Variables)
                {
                    processed[$"context.{variable.Key}"] = variable.Value;
                }
            }

            // Runtime değişkenlerini ekle;
            foreach (var variable in command.RuntimeVariables)
            {
                processed[$"runtime.{variable.Key}"] = variable.Value;
            }

            await Task.CompletedTask;
            return processed;
        }

        private async Task<ActionExecutionResult> ExecuteActionAsync(
            CommandAction action,
            Dictionary<string, object> parameters,
            ExecutionContext context,
            CancellationToken cancellationToken)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(action.TimeoutMs);

                // Parametreleri yerine koy;
                var processedCommand = ReplaceParameters(action.Command, parameters);
                var processedTarget = ReplaceParameters(action.Target, parameters);

                // Eylem türüne göre yürüt;
                object result = null;
                bool success = false;

                switch (action.ActionType)
                {
                    case ActionType.SystemCommand:
                        result = await ExecuteSystemCommandAsync(processedCommand, cts.Token);
                        success = result != null;
                        break;

                    case ActionType.APICall:
                        result = await ExecuteApiCallAsync(processedCommand, processedTarget, action.Parameters, cts.Token);
                        success = result != null;
                        break;

                    case ActionType.Script:
                        result = await ExecuteScriptAsync(processedCommand, parameters, cts.Token);
                        success = result != null;
                        break;

                    case ActionType.EventTrigger:
                        await TriggerEventAsync(processedCommand, parameters, cts.Token);
                        success = true;
                        break;

                    case ActionType.DataOperation:
                        result = await PerformDataOperationAsync(processedCommand, parameters, cts.Token);
                        success = result != null;
                        break;

                    default:
                        throw new NotSupportedException($"Action type '{action.ActionType}' is not supported");
                }

                // Beklenen sonuç kontrolü;
                var matchesExpected = string.IsNullOrWhiteSpace(action.ExpectedResult) ||
                                     (result?.ToString()?.Contains(action.ExpectedResult) ?? false);

                return new ActionExecutionResult;
                {
                    ActionId = action.Id,
                    IsSuccess = success && matchesExpected,
                    Result = result,
                    ExecutionTime = (DateTime.UtcNow - startTime).TotalMilliseconds,
                    Error = success && !matchesExpected ? "Result does not match expected value" : null,
                    StopOnFailure = action.ActionType == ActionType.SystemCommand // Sistem komutları başarısız olursa dur;
                };
            }
            catch (OperationCanceledException)
            {
                return new ActionExecutionResult;
                {
                    ActionId = action.Id,
                    IsSuccess = false,
                    Error = "Action timed out",
                    ExecutionTime = (DateTime.UtcNow - startTime).TotalMilliseconds,
                    StopOnFailure = true;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Action execution failed: {ex.Message}", ex);

                return new ActionExecutionResult;
                {
                    ActionId = action.Id,
                    IsSuccess = false,
                    Error = ex.Message,
                    ExecutionTime = (DateTime.UtcNow - startTime).TotalMilliseconds,
                    StopOnFailure = action.ActionType == ActionType.SystemCommand;
                };
            }
        }

        private string ReplaceParameters(string input, Dictionary<string, object> parameters)
        {
            if (string.IsNullOrWhiteSpace(input) || parameters == null)
                return input;

            foreach (var param in parameters)
            {
                var placeholder = $"{{{param.Key}}}";
                if (input.Contains(placeholder))
                {
                    input = input.Replace(placeholder, param.Value?.ToString() ?? string.Empty);
                }
            }

            return input;
        }

        private async Task<object> ExecuteSystemCommandAsync(string command, CancellationToken cancellationToken)
        {
            // Sistem komutları burada yürütülür;
            // Örnek implementasyon;
            await Task.Delay(100, cancellationToken); // Simülasyon;
            return $"System command executed: {command}";
        }

        private async Task<object> ExecuteApiCallAsync(string url, string method, Dictionary<string, string> parameters, CancellationToken cancellationToken)
        {
            // API çağrıları burada yürütülür;
            await Task.Delay(200, cancellationToken); // Simülasyon;
            return $"API call completed: {method} {url}";
        }

        private async Task<object> ExecuteScriptAsync(string script, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Script yürütme burada gerçekleşir;
            await Task.Delay(150, cancellationToken); // Simülasyon;
            return $"Script executed with {parameters.Count} parameters";
        }

        private async Task TriggerEventAsync(string eventName, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            await _eventBus.PublishAsync(new CustomEvent;
            {
                Name = eventName,
                Data = parameters,
                Timestamp = DateTime.UtcNow;
            }, cancellationToken);
        }

        private async Task<object> PerformDataOperationAsync(string operation, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Veri operasyonları burada yürütülür;
            await Task.Delay(100, cancellationToken); // Simülasyon;
            return $"Data operation '{operation}' completed";
        }

        private async Task<bool> EvaluateConditionAsync(string condition, Dictionary<string, object> parameters, CancellationToken cancellationToken)
        {
            // Basit koşul değerlendirmesi;
            // Gerçek implementasyonda bir script engine kullanılabilir;
            await Task.CompletedTask;

            try
            {
                // Örnek: {param} == "value"
                var processedCondition = ReplaceParameters(condition, parameters);

                // Burada daha gelişmiş bir condition parser kullanılmalı;
                // Şimdilik basit true/false döndürüyoruz;
                return processedCondition.Contains("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private async Task UpdateCommandStatisticsAsync(string commandId, bool success, CancellationToken cancellationToken)
        {
            if (_commandCache.TryGetValue(commandId, out var command))
            {
                command.LastExecuted = DateTime.UtcNow;
                command.ExecutionCount++;
                command.UpdatedAt = DateTime.UtcNow;

                // Başarı istatistiklerini güncelle;
                // Burada daha detaylı istatistik tutulabilir;

                await Task.CompletedTask;
            }
        }

        private CommandExecutionResult BuildExecutionResult(
            QuickCommand command,
            string executionId,
            DateTime startTime,
            bool success,
            List<ActionExecutionResult> actionResults,
            Dictionary<string, object> parameters)
        {
            var executionTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
            var successfulActions = actionResults.Count(r => r.IsSuccess);
            var failedActions = actionResults.Count(r => !r.IsSuccess);

            return new CommandExecutionResult;
            {
                ExecutionId = executionId,
                CommandId = command.Id,
                CommandName = command.Name,
                IsSuccess = success,
                ExecutionTime = executionTime,
                StartTime = startTime,
                EndTime = DateTime.UtcNow,
                ActionResults = actionResults,
                SuccessfulActions = successfulActions,
                FailedActions = failedActions,
                TotalActions = actionResults.Count,
                Parameters = parameters,
                Message = success ? "Command executed successfully" : "Command execution failed"
            };
        }

        private async Task PerformCacheMaintenanceAsync()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCacheCleanup).TotalMinutes < _configuration.CacheCleanupIntervalMinutes)
                return;

            await _cacheLock.WaitAsync();
            try
            {
                var expiredCommands = _commandCache.Where(kvp =>
                {
                    var command = kvp.Value;
                    if (!command.CachePolicy.Enabled)
                        return false;

                    var cacheAge = now - (command.LastExecuted ?? command.UpdatedAt);
                    return cacheAge > command.CachePolicy.CleanupThreshold;
                }).ToList();

                foreach (var expired in expiredCommands)
                {
                    _commandCache.TryRemove(expired.Key, out _);
                    _logger.LogDebug($"Removed expired command from cache: {expired.Value.Name}");
                }

                _lastCacheCleanup = now;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        private async Task<double> CalculateAverageExecutionTimeAsync(string commandId, CancellationToken cancellationToken)
        {
            // Gerçek implementasyonda veritabanından geçmiş yürütme süreleri alınır;
            await Task.CompletedTask;
            return 150.0; // Örnek değer;
        }

        private async Task<double> CalculateSuccessRateAsync(string commandId, CancellationToken cancellationToken)
        {
            // Gerçek implementasyonda veritabanından başarı oranı hesaplanır;
            await Task.CompletedTask;
            return 0.95; // Örnek değer;
        }

        private async Task<CommandOperationResult> SetCommandEnabledStatusAsync(
            string commandId, bool enabled, string userId, CancellationToken cancellationToken)
        {
            try
            {
                await _cacheLock.WaitAsync(cancellationToken);
                try
                {
                    if (!_commandCache.TryGetValue(commandId, out var command))
                    {
                        return CommandOperationResult.Failure($"Command with id '{commandId}' not found");
                    }

                    // Yetki kontrolü;
                    if (!await HasPermissionToModifyAsync(userId, command))
                    {
                        return CommandOperationResult.Failure(
                            $"User '{userId}' does not have permission to modify this command");
                    }

                    if (command.IsEnabled == enabled)
                    {
                        return CommandOperationResult.Success(commandId,
                            $"Command is already {(enabled ? "enabled" : "disabled")}");
                    }

                    command.IsEnabled = enabled;
                    command.UpdatedAt = DateTime.UtcNow;

                    // Olay yayınla;
                    var eventType = enabled ? "Enabled" : "Disabled";
                    await _eventBus.PublishAsync(new QuickCommandStatusChangedEvent;
                    {
                        CommandId = commandId,
                        CommandName = command.Name,
                        UserId = userId,
                        NewStatus = enabled ? "Enabled" : "Disabled",
                        Timestamp = DateTime.UtcNow;
                    }, cancellationToken);

                    _logger.LogInformation($"Quick command {eventType}: {command.Name} (ID: {commandId})");

                    return CommandOperationResult.Success(commandId);
                }
                finally
                {
                    _cacheLock.Release();
                }
            }
            catch (Exception ex)
            {
                await _errorHandler.HandleErrorAsync(ex, new ErrorContext;
                {
                    Module = "QuickCommandService",
                    Operation = enabled ? "EnableCommand" : "DisableCommand",
                    UserId = userId,
                    AdditionalData = new { CommandId = commandId }
                });

                return CommandOperationResult.Failure($"Failed to {(enabled ? "enable" : "disable")} command: {ex.Message}");
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
                    _cacheLock?.Dispose();
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

    /// <summary>
    /// Hızlı komut servis konfigürasyonu;
    /// </summary>
    public class QuickCommandConfiguration;
    {
        public static QuickCommandConfiguration Default => new QuickCommandConfiguration();

        public int MaxConcurrentExecutions { get; set; } = 5;
        public int ExecutionLockTimeoutSeconds { get; set; } = 30;
        public int CacheCleanupIntervalMinutes { get; set; } = 5;
        public bool EnablePerformanceMetrics { get; set; } = true;
        public bool EnableExecutionLogging { get; set; } = true;
        public int MaxCommandHistory { get; set; } = 1000;
        public TimeSpan DefaultCommandTimeout { get; set; } = TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Komut öncelik seviyeleri;
    /// </summary>
    public enum CommandPriority;
    {
        Low = 0,
        Normal = 1,
        High = 2,
        Critical = 3;
    }

    /// <summary>
    /// Eylem türleri;
    /// </summary>
    public enum ActionType;
    {
        SystemCommand = 0,
        APICall = 1,
        Script = 2,
        EventTrigger = 3,
        DataOperation = 4;
    }

    /// <summary>
    /// Parametre türleri;
    /// </summary>
    public enum ParameterType;
    {
        String = 0,
        Number = 1,
        Boolean = 2,
        Integer = 3,
        DateTime = 4;
    }

    /// <summary>
    /// Yürütme bağlamı;
    /// </summary>
    public class ExecutionContext;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public Dictionary<string, object> Variables { get; set; } = new Dictionary<string, object>();
        public string Environment { get; set; }

        public static ExecutionContext System => new ExecutionContext;
        {
            Environment = "System"
        };

        public static ExecutionContext User => new ExecutionContext;
        {
            Environment = "User"
        };
    }

    /// <summary>
    /// Önbellek politikası;
    /// </summary>
    public class CachePolicy;
    {
        public static CachePolicy Default => new CachePolicy;
        {
            Enabled = true,
            Duration = TimeSpan.FromMinutes(30),
            InvalidateOnExpiry = true,
            CleanupThreshold = TimeSpan.FromHours(24)
        };

        public bool Enabled { get; set; }
        public TimeSpan Duration { get; set; }
        public bool InvalidateOnExpiry { get; set; }
        public TimeSpan CleanupThreshold { get; set; }
    }

    /// <summary>
    /// Doğrulama sonucu;
    /// </summary>
    public class ValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Komut operasyon sonucu;
    /// </summary>
    public class CommandOperationResult;
    {
        public bool IsSuccess { get; set; }
        public string CommandId { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static CommandOperationResult Success(string commandId, string message = null)
        {
            return new CommandOperationResult;
            {
                IsSuccess = true,
                CommandId = commandId,
                Message = message ?? "Operation completed successfully"
            };
        }

        public static CommandOperationResult Failure(string error)
        {
            return new CommandOperationResult;
            {
                IsSuccess = false,
                Error = error;
            };
        }
    }

    /// <summary>
    /// Komut yürütme sonucu;
    /// </summary>
    public class CommandExecutionResult;
    {
        public string ExecutionId { get; set; }
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public bool IsSuccess { get; set; }
        public double ExecutionTime { get; set; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public List<ActionExecutionResult> ActionResults { get; set; }
        public int SuccessfulActions { get; set; }
        public int FailedActions { get; set; }
        public int TotalActions { get; set; }
        public Dictionary<string, object> Parameters { get; set; }
        public string Message { get; set; }
        public string Error { get; set; }

        public static CommandExecutionResult Success(
            string executionId, string commandId, string commandName,
            List<ActionExecutionResult> actionResults, Dictionary<string, object> parameters)
        {
            return new CommandExecutionResult;
            {
                ExecutionId = executionId,
                CommandId = commandId,
                CommandName = commandName,
                IsSuccess = true,
                ActionResults = actionResults,
                Parameters = parameters,
                SuccessfulActions = actionResults?.Count(r => r.IsSuccess) ?? 0,
                FailedActions = actionResults?.Count(r => !r.IsSuccess) ?? 0,
                TotalActions = actionResults?.Count ?? 0,
                Message = "Command executed successfully",
                StartTime = DateTime.UtcNow.AddMilliseconds(-100), // Örnek;
                EndTime = DateTime.UtcNow,
                ExecutionTime = 100;
            };
        }

        public static CommandExecutionResult Failure(string error, string executionId = null)
        {
            return new CommandExecutionResult;
            {
                ExecutionId = executionId ?? Guid.NewGuid().ToString(),
                IsSuccess = false,
                Error = error,
                Message = "Command execution failed",
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                ExecutionTime = 0;
            };
        }

        public static CommandExecutionResult NotFound(string message)
        {
            return new CommandExecutionResult;
            {
                ExecutionId = Guid.NewGuid().ToString(),
                IsSuccess = false,
                Error = "Command not found",
                Message = message,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                ExecutionTime = 0;
            };
        }

        public static CommandExecutionResult Cancelled(string executionId)
        {
            return new CommandExecutionResult;
            {
                ExecutionId = executionId,
                IsSuccess = false,
                Error = "Execution cancelled",
                Message = "Command execution was cancelled",
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                ExecutionTime = 0;
            };
        }
    }

    /// <summary>
    /// Eylem yürütme sonucu;
    /// </summary>
    public class ActionExecutionResult;
    {
        public string ActionId { get; set; }
        public bool IsSuccess { get; set; }
        public object Result { get; set; }
        public double ExecutionTime { get; set; }
        public string Error { get; set; }
        public bool StopOnFailure { get; set; }
    }

    /// <summary>
    /// Bağımlılık kontrol sonucu;
    /// </summary>
    public class DependencyCheckResult;
    {
        public string CommandId { get; set; }
        public bool HasAllDependencies { get; set; }
        public List<string> MissingDependencies { get; set; } = new List<string>();
        public List<string> AvailableDependencies { get; set; } = new List<string>();

        public static DependencyCheckResult NotFound(string message)
        {
            return new DependencyCheckResult;
            {
                HasAllDependencies = false,
                MissingDependencies = { message }
            };
        }
    }

    /// <summary>
    /// Komut istatistikleri;
    /// </summary>
    public class CommandStatistics;
    {
        public string CommandId { get; set; }
        public int ExecutionCount { get; set; }
        public DateTime? LastExecuted { get; set; }
        public double AverageExecutionTime { get; set; }
        public double SuccessRate { get; set; }
        public int CategoryRank { get; set; }
        public int TotalCategoryCommands { get; set; }
    }

    /// <summary>
    /// Özel olay sınıfı;
    /// </summary>
    public class CustomEvent : IEvent;
    {
        public string Name { get; set; }
        public Dictionary<string, object> Data { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hızlı komut kayıt olayı;
    /// </summary>
    public class QuickCommandRegisteredEvent : IEvent;
    {
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hızlı komut güncelleme olayı;
    /// </summary>
    public class QuickCommandUpdatedEvent : IEvent;
    {
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hızlı komut silme olayı;
    /// </summary>
    public class QuickCommandDeletedEvent : IEvent;
    {
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public string UserId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hızlı komut yürütme olayı;
    /// </summary>
    public class QuickCommandExecutedEvent : IEvent;
    {
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public string ExecutionId { get; set; }
        public string UserId { get; set; }
        public bool Success { get; set; }
        public double ExecutionTime { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Hızlı komut durum değişikliği olayı;
    /// </summary>
    public class QuickCommandStatusChangedEvent : IEvent;
    {
        public string CommandId { get; set; }
        public string CommandName { get; set; }
        public string UserId { get; set; }
        public string NewStatus { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

namespace NEDA.Interface.TextInput.QuickCommands.Contracts;
{
    /// <summary>
    /// Hızlı komut servisi interface'i;
    /// </summary>
    public interface IQuickCommandService : IDisposable
    {
        /// <summary>
        /// Hızlı komutu kaydeder;
        /// </summary>
        Task<CommandOperationResult> RegisterCommandAsync(
            QuickCommand command,
            string userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Hızlı komutu günceller;
        /// </summary>
        Task<CommandOperationResult> UpdateCommandAsync(
            string commandId,
            QuickCommand updatedCommand,
            string userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Hızlı komutu yürütür;
        /// </summary>
        Task<CommandExecutionResult> ExecuteCommandAsync(
            string trigger,
            Dictionary<string, object> parameters = null,
            string userId = null,
            ExecutionContext context = null,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Tetikleyiciye göre komut bulur;
        /// </summary>
        Task<QuickCommand> FindCommandByTriggerAsync(
            string trigger,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Kategoriye göre komutları listeler;
        /// </summary>
        Task<IEnumerable<QuickCommand>> GetCommandsByCategoryAsync(
            string category,
            bool includeDisabled = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Komutu devre dışı bırakır;
        /// </summary>
        Task<CommandOperationResult> DisableCommandAsync(
            string commandId,
            string userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Komutu etkinleştirir;
        /// </summary>
        Task<CommandOperationResult> EnableCommandAsync(
            string commandId,
            string userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Komutu siler;
        /// </summary>
        Task<CommandOperationResult> DeleteCommandAsync(
            string commandId,
            string userId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Komut bağımlılıklarını kontrol eder;
        /// </summary>
        Task<DependencyCheckResult> CheckDependenciesAsync(
            string commandId,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Komut istatistiklerini alır;
        /// </summary>
        Task<CommandStatistics> GetCommandStatisticsAsync(
            string commandId,
            CancellationToken cancellationToken = default);
    }
}
