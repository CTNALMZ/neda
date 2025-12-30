using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.API.DTOs;
using NEDA.API.Common;

namespace NEDA.API.ClientSDK;
{
    /// <summary>
    /// Main client class for NEDA API - provides high-level access to all NEDA services;
    /// with unified error handling, session management, and performance monitoring;
    /// </summary>
    public class NEDAClient : INEDAClient, IDisposable;
    {
        #region Private Fields;
        private readonly ILogger _logger;
        private readonly APIWrapper _apiWrapper;
        private readonly ClientConfiguration _config;
        private readonly PerformanceMonitor _performanceMonitor;
        private readonly List<ClientEvent> _eventHistory;
        private readonly object _lockObject = new object();
        private bool _disposed = false;
        private bool _initialized = false;
        private string _clientId;
        private ClientState _currentState;
        #endregion;

        #region Public Properties;
        public string ClientId => _clientId;
        public string ClientVersion { get; private set; }
        public ClientState CurrentState => _currentState;
        public ClientInfo ClientInfo { get; private set; }
        public UserSession CurrentSession { get; private set; }
        public bool IsConnected => _apiWrapper?.IsConnected == true;
        public bool IsAuthenticated => CurrentSession?.IsValid == true;
        public TimeSpan Uptime => DateTime.UtcNow - _startTime;
        public event EventHandler<ClientStateChangedEventArgs> StateChanged;
        public event EventHandler<SessionEventArgs> SessionEvent;
        public event EventHandler<ClientErrorEventArgs> ErrorOccurred;
        #endregion;

        #region Private Fields;
        private readonly DateTime _startTime;
        #endregion;

        #region Constructors;
        public NEDAClient(ILogger logger, ClientConfiguration config = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config ?? new ClientConfiguration();

            InitializeClient();
        }

        public NEDAClient(string apiBaseUrl, ILogger logger, ClientConfiguration config = null)
            : this(logger, config)
        {
            if (string.IsNullOrWhiteSpace(apiBaseUrl))
                throw new ArgumentException("API base URL cannot be null or empty", nameof(apiBaseUrl));

            _apiWrapper = new APIWrapper(apiBaseUrl, logger, CreateWrapperSettings());
            HookApiEvents();
        }

        public NEDAClient(APIWrapper apiWrapper, ILogger logger, ClientConfiguration config = null)
            : this(logger, config)
        {
            _apiWrapper = apiWrapper ?? throw new ArgumentNullException(nameof(apiWrapper));
            HookApiEvents();
        }
        #endregion;

        #region Initialization and Lifecycle;
        private void InitializeClient()
        {
            _clientId = GenerateClientId();
            ClientVersion = "1.0.0";
            _currentState = ClientState.Created;
            _startTime = DateTime.UtcNow;
            _eventHistory = new List<ClientEvent>();
            _performanceMonitor = new PerformanceMonitor();

            ClientInfo = new ClientInfo;
            {
                ClientId = _clientId,
                Version = ClientVersion,
                Platform = Environment.OSVersion.Platform.ToString(),
                StartTime = _startTime;
            };

            _logger.LogInformation($"NEDA Client initialized: {_clientId}");
        }

        private void HookApiEvents()
        {
            _apiWrapper.StatusChanged += OnApiStatusChanged;
            _apiWrapper.RequestCompleted += OnApiRequestCompleted;
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (_initialized)
            {
                return;
            }

            try
            {
                UpdateState(ClientState.Initializing);

                // Initialize API connection;
                var connected = await _apiWrapper.ConnectAsync(cancellationToken);
                if (!connected)
                {
                    throw new NEDAClientException("Failed to connect to NEDA API");
                }

                // Load client configuration;
                await LoadClientConfigurationAsync(cancellationToken);

                _initialized = true;
                UpdateState(ClientState.Ready);

                LogClientEvent(ClientEventType.Initialized, "Client initialized successfully");

                _logger.LogInformation($"NEDA Client fully initialized and ready: {_clientId}");
            }
            catch (Exception ex)
            {
                UpdateState(ClientState.Error);
                _logger.LogError(ex, "Failed to initialize NEDA Client");
                throw new NEDAClientException("Initialization failed", ex);
            }
        }

        public async Task ShutdownAsync()
        {
            ValidateNotDisposed();

            try
            {
                UpdateState(ClientState.ShuttingDown);

                // Save client state;
                await SaveClientStateAsync();

                // Close session if active;
                if (CurrentSession != null)
                {
                    await LogoutAsync();
                }

                // Disconnect from API;
                await _apiWrapper.DisconnectAsync();

                UpdateState(ClientState.Shutdown);

                LogClientEvent(ClientEventType.Shutdown, "Client shutdown completed");

                _logger.LogInformation("NEDA Client shutdown completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during client shutdown");
                throw new NEDAClientException("Shutdown failed", ex);
            }
        }
        #endregion;

        #region Authentication and Session Management;
        public async Task<AuthenticationResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateCredentials(username, password);

            try
            {
                UpdateState(ClientState.Authenticating);

                var authResponse = await _apiWrapper.AuthenticateAsync(username, password, cancellationToken);

                if (authResponse.Success)
                {
                    CurrentSession = new UserSession;
                    {
                        SessionId = authResponse.SessionId,
                        UserId = authResponse.UserId,
                        Username = username,
                        AccessToken = authResponse.AccessToken,
                        ExpiresAt = DateTime.UtcNow.AddSeconds(authResponse.ExpiresIn),
                        CreatedAt = DateTime.UtcNow;
                    };

                    UpdateState(ClientState.Ready);

                    OnSessionEvent(new SessionEventArgs;
                    {
                        EventType = SessionEventType.Login,
                        Session = CurrentSession,
                        Message = $"User {username} logged in successfully"
                    });

                    LogClientEvent(ClientEventType.UserLoggedIn, $"User {username} authenticated");

                    return new AuthenticationResult;
                    {
                        Success = true,
                        Session = CurrentSession,
                        Message = "Login successful"
                    };
                }

                UpdateState(ClientState.Ready);
                return new AuthenticationResult;
                {
                    Success = false,
                    Message = authResponse.Message ?? "Authentication failed"
                };
            }
            catch (Exception ex)
            {
                UpdateState(ClientState.Ready);
                _logger.LogError(ex, $"Login failed for user: {username}");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.Authentication,
                    Message = $"Login failed: {ex.Message}",
                    Exception = ex;
                });

                return new AuthenticationResult;
                {
                    Success = false,
                    Message = $"Login failed: {ex.Message}"
                };
            }
        }

        public async Task LogoutAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (CurrentSession == null)
            {
                return;
            }

            try
            {
                var previousSession = CurrentSession;
                CurrentSession = null;

                _apiWrapper.Logout();

                OnSessionEvent(new SessionEventArgs;
                {
                    EventType = SessionEventType.Logout,
                    Session = previousSession,
                    Message = $"User {previousSession.Username} logged out"
                });

                LogClientEvent(ClientEventType.UserLoggedOut, $"User {previousSession.Username} logged out");

                _logger.LogInformation($"User logged out: {previousSession.Username}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during logout");
                throw new NEDAClientException("Logout failed", ex);
            }
        }

        public async Task<SessionValidationResult> ValidateSessionAsync()
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (CurrentSession == null || !CurrentSession.IsValid)
            {
                return new SessionValidationResult { IsValid = false, Reason = "No active session" };
            }

            try
            {
                // Check if token is about to expire;
                if (CurrentSession.ExpiresAt.Subtract(DateTime.UtcNow) < TimeSpan.FromMinutes(5))
                {
                    var refreshed = await _apiWrapper.RefreshTokenAsync();
                    if (refreshed)
                    {
                        CurrentSession.ExpiresAt = DateTime.UtcNow.AddHours(1); // Assume 1 hour refresh;
                        return new SessionValidationResult { IsValid = true, WasRefreshed = true };
                    }
                    else;
                    {
                        CurrentSession = null;
                        return new SessionValidationResult { IsValid = false, Reason = "Token refresh failed" };
                    }
                }

                return new SessionValidationResult { IsValid = true };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Session validation failed");
                return new SessionValidationResult { IsValid = false, Reason = $"Validation error: {ex.Message}" };
            }
        }
        #endregion;

        #region Project Management;
        public async Task<ProjectOperationResult> CreateProjectAsync(string projectName, string description = null, ProjectSettings settings = null, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidateProjectName(projectName);

            try
            {
                var request = new CreateProjectRequest;
                {
                    Name = projectName,
                    Description = description,
                    Settings = settings ?? new ProjectSettings(),
                    ClientId = _clientId;
                };

                var response = await _apiWrapper.CreateProjectAsync(request, cancellationToken);

                if (response.Success)
                {
                    LogClientEvent(ClientEventType.ProjectCreated, $"Project created: {projectName}");

                    return new ProjectOperationResult;
                    {
                        Success = true,
                        Project = response.Project,
                        Message = "Project created successfully"
                    };
                }

                return new ProjectOperationResult;
                {
                    Success = false,
                    Message = response.Message ?? "Failed to create project"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create project: {projectName}");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.ProjectOperation,
                    Message = $"Project creation failed: {ex.Message}",
                    Exception = ex;
                });

                return new ProjectOperationResult;
                {
                    Success = false,
                    Message = $"Project creation failed: {ex.Message}"
                };
            }
        }

        public async Task<ProjectOperationResult> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidateProjectId(projectId);

            try
            {
                var response = await _apiWrapper.GetProjectAsync(projectId, cancellationToken);

                if (response.Success)
                {
                    return new ProjectOperationResult;
                    {
                        Success = true,
                        Project = response.Project,
                        Message = "Project retrieved successfully"
                    };
                }

                return new ProjectOperationResult;
                {
                    Success = false,
                    Message = response.Message ?? "Failed to retrieve project"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get project: {projectId}");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.ProjectOperation,
                    Message = $"Project retrieval failed: {ex.Message}",
                    Exception = ex;
                });

                return new ProjectOperationResult;
                {
                    Success = false,
                    Message = $"Project retrieval failed: {ex.Message}"
                };
            }
        }

        public async Task<ProjectListResult> GetProjectsAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidatePagination(page, pageSize);

            try
            {
                var response = await _apiWrapper.GetProjectsAsync(page, pageSize, cancellationToken);

                return new ProjectListResult;
                {
                    Success = true,
                    Projects = response.Projects,
                    TotalCount = response.TotalCount,
                    Page = page,
                    PageSize = pageSize,
                    Message = "Projects retrieved successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get projects list");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.ProjectOperation,
                    Message = $"Projects retrieval failed: {ex.Message}",
                    Exception = ex;
                });

                return new ProjectListResult;
                {
                    Success = false,
                    Message = $"Projects retrieval failed: {ex.Message}"
                };
            }
        }

        public async Task<ProjectOperationResult> UpdateProjectAsync(string projectId, UpdateProjectRequest updateRequest, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidateProjectId(projectId);
            ValidateUpdateRequest(updateRequest);

            try
            {
                var response = await _apiWrapper.UpdateProjectAsync(projectId, updateRequest, cancellationToken);

                if (response.Success)
                {
                    LogClientEvent(ClientEventType.ProjectUpdated, $"Project updated: {projectId}");

                    return new ProjectOperationResult;
                    {
                        Success = true,
                        Project = response.Project,
                        Message = "Project updated successfully"
                    };
                }

                return new ProjectOperationResult;
                {
                    Success = false,
                    Message = response.Message ?? "Failed to update project"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update project: {projectId}");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.ProjectOperation,
                    Message = $"Project update failed: {ex.Message}",
                    Exception = ex;
                });

                return new ProjectOperationResult;
                {
                    Success = false,
                    Message = $"Project update failed: {ex.Message}"
                };
            }
        }

        public async Task<OperationResult> DeleteProjectAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidateProjectId(projectId);

            try
            {
                var response = await _apiWrapper.DeleteProjectAsync(projectId, cancellationToken);

                if (response.Success)
                {
                    LogClientEvent(ClientEventType.ProjectDeleted, $"Project deleted: {projectId}");

                    return new OperationResult;
                    {
                        Success = true,
                        Message = "Project deleted successfully"
                    };
                }

                return new OperationResult;
                {
                    Success = false,
                    Message = response.Message ?? "Failed to delete project"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete project: {projectId}");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.ProjectOperation,
                    Message = $"Project deletion failed: {ex.Message}",
                    Exception = ex;
                });

                return new OperationResult;
                {
                    Success = false,
                    Message = $"Project deletion failed: {ex.Message}"
                };
            }
        }
        #endregion;

        #region Command Execution;
        public async Task<CommandExecutionResult> ExecuteCommandAsync(string commandName, Dictionary<string, object> parameters = null, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidateCommandName(commandName);

            try
            {
                var commandRequest = new CommandRequest;
                {
                    CommandName = commandName,
                    Parameters = parameters ?? new Dictionary<string, object>(),
                    ClientId = _clientId,
                    SessionId = CurrentSession?.SessionId;
                };

                var response = await _apiWrapper.ExecuteCommandAsync(commandRequest, cancellationToken);

                LogClientEvent(ClientEventType.CommandExecuted, $"Command executed: {commandName}");

                return new CommandExecutionResult;
                {
                    Success = response.Success,
                    CommandId = response.CommandId,
                    Result = response.Result,
                    Message = response.Message,
                    ExecutionTime = response.ExecutionTime;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Command execution failed: {commandName}");

                OnErrorOccurred(new ClientErrorEventArgs;
                {
                    ErrorType = ClientErrorType.CommandExecution,
                    Message = $"Command execution failed: {ex.Message}",
                    Exception = ex;
                });

                return new CommandExecutionResult;
                {
                    Success = false,
                    Message = $"Command execution failed: {ex.Message}"
                };
            }
        }

        public async Task<CommandStatusResult> GetCommandStatusAsync(string commandId, CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();
            ValidateAuthenticated();
            ValidateCommandId(commandId);

            try
            {
                var response = await _apiWrapper.GetCommandStatusAsync(commandId, cancellationToken);

                return new CommandStatusResult;
                {
                    Success = true,
                    CommandId = response.CommandId,
                    Status = response.Status,
                    Progress = response.Progress,
                    Result = response.Result,
                    Message = response.Message;
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get command status: {commandId}");
                return new CommandStatusResult;
                {
                    Success = false,
                    Message = $"Failed to get command status: {ex.Message}"
                };
            }
        }
        #endregion;

        #region System Operations;
        public async Task<SystemStatusResult> GetSystemStatusAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                var response = await _apiWrapper.GetSystemStatusAsync(cancellationToken);

                return new SystemStatusResult;
                {
                    Success = true,
                    SystemStatus = response.SystemStatus,
                    Services = response.Services,
                    Message = "System status retrieved successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get system status");
                return new SystemStatusResult;
                {
                    Success = false,
                    Message = $"Failed to get system status: {ex.Message}"
                };
            }
        }

        public async Task<PerformanceMetricsResult> GetPerformanceMetricsAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            try
            {
                var response = await _apiWrapper.GetPerformanceMetricsAsync(cancellationToken);

                // Combine with client-side metrics;
                var combinedMetrics = new PerformanceMetrics;
                {
                    CpuUsage = response.Metrics.CpuUsage,
                    MemoryUsage = response.Metrics.MemoryUsage,
                    NetworkUsage = response.Metrics.NetworkUsage,
                    ClientMetrics = _performanceMonitor.GetCurrentMetrics()
                };

                return new PerformanceMetricsResult;
                {
                    Success = true,
                    Metrics = combinedMetrics,
                    Message = "Performance metrics retrieved successfully"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get performance metrics");
                return new PerformanceMetricsResult;
                {
                    Success = false,
                    Message = $"Failed to get performance metrics: {ex.Message}"
                };
            }
        }
        #endregion;

        #region Utility Methods;
        private APIWrapperSettings CreateWrapperSettings()
        {
            return new APIWrapperSettings;
            {
                DefaultTimeout = _config.ApiTimeout,
                MaxRetryAttempts = _config.MaxRetryAttempts,
                CacheDurationMinutes = _config.CacheDurationMinutes,
                MaxRequestsPerMinute = _config.MaxRequestsPerMinute;
            };
        }

        private string GenerateClientId()
        {
            return $"{Environment.MachineName}_{Guid.NewGuid():N}";
        }

        private void UpdateState(ClientState newState)
        {
            var oldState = _currentState;
            _currentState = newState;

            if (oldState != newState)
            {
                StateChanged?.Invoke(this, new ClientStateChangedEventArgs;
                {
                    OldState = oldState,
                    NewState = newState,
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        private void LogClientEvent(ClientEventType eventType, string message)
        {
            var clientEvent = new ClientEvent;
            {
                EventId = Guid.NewGuid().ToString(),
                EventType = eventType,
                Message = message,
                Timestamp = DateTime.UtcNow,
                ClientId = _clientId,
                SessionId = CurrentSession?.SessionId;
            };

            lock (_lockObject)
            {
                _eventHistory.Add(clientEvent);

                // Keep only last 1000 events;
                if (_eventHistory.Count > 1000)
                {
                    _eventHistory.RemoveAt(0);
                }
            }

            _logger.LogDebug($"Client event: {eventType} - {message}");
        }

        private async Task LoadClientConfigurationAsync(CancellationToken cancellationToken)
        {
            // In a real implementation, this would load from local storage or configuration service;
            await Task.CompletedTask;
        }

        private async Task SaveClientStateAsync()
        {
            // In a real implementation, this would save to local storage;
            await Task.CompletedTask;
        }

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NEDAClient));
        }

        private void ValidateInitialized()
        {
            if (!_initialized)
                throw new NEDAClientException("Client not initialized. Call InitializeAsync first.");
        }

        private void ValidateAuthenticated()
        {
            if (!IsAuthenticated)
                throw new NEDAClientException("Authentication required for this operation.");
        }

        private void ValidateCredentials(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username))
                throw new ArgumentException("Username cannot be empty", nameof(username));
            if (string.IsNullOrWhiteSpace(password))
                throw new ArgumentException("Password cannot be empty", nameof(password));
        }

        private void ValidateProjectName(string projectName)
        {
            if (string.IsNullOrWhiteSpace(projectName))
                throw new ArgumentException("Project name cannot be empty", nameof(projectName));
        }

        private void ValidateProjectId(string projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                throw new ArgumentException("Project ID cannot be empty", nameof(projectId));
        }

        private void ValidatePagination(int page, int pageSize)
        {
            if (page < 1)
                throw new ArgumentException("Page must be positive", nameof(page));
            if (pageSize < 1 || pageSize > 1000)
                throw new ArgumentException("Page size must be between 1 and 1000", nameof(pageSize));
        }

        private void ValidateUpdateRequest(UpdateProjectRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
        }

        private void ValidateCommandName(string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName))
                throw new ArgumentException("Command name cannot be empty", nameof(commandName));
        }

        private void ValidateCommandId(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
                throw new ArgumentException("Command ID cannot be empty", nameof(commandId));
        }
        #endregion;

        #region Event Handlers;
        private void OnApiStatusChanged(object sender, APIStatusChangedEventArgs e)
        {
            // Map API status to client state;
            var newState = e.NewStatus switch;
            {
                APIStatus.Connected => ClientState.Ready,
                APIStatus.Disconnected => ClientState.Disconnected,
                APIStatus.ConnectionFailed => ClientState.ConnectionError,
                _ => _currentState;
            };

            if (newState != _currentState)
            {
                UpdateState(newState);
            }
        }

        private void OnApiRequestCompleted(object sender, RequestCompletedEventArgs e)
        {
            _performanceMonitor.RecordRequest(e.Duration, e.StatusCode);
        }

        protected virtual void OnStateChanged(ClientStateChangedEventArgs e)
        {
            StateChanged?.Invoke(this, e);
        }

        protected virtual void OnSessionEvent(SessionEventArgs e)
        {
            SessionEvent?.Invoke(this, e);
        }

        protected virtual void OnErrorOccurred(ClientErrorEventArgs e)
        {
            ErrorOccurred?.Invoke(this, e);
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
                    ShutdownAsync().Wait(10000); // Wait up to 10 seconds for shutdown;
                    _apiWrapper?.Dispose();
                    _logger.LogInformation("NEDA Client disposed");
                }

                _disposed = true;
            }
        }

        ~NEDAClient()
        {
            Dispose(false);
        }
        #endregion;
    }

    #region Supporting Classes and Enums;
    public class ClientConfiguration;
    {
        public TimeSpan ApiTimeout { get; set; } = TimeSpan.FromSeconds(30);
        public int MaxRetryAttempts { get; set; } = 3;
        public int CacheDurationMinutes { get; set; } = 5;
        public int MaxRequestsPerMinute { get; set; } = 60;
        public bool AutoReconnect { get; set; } = true;
        public TimeSpan ReconnectInterval { get; set; } = TimeSpan.FromSeconds(5);
    }

    public enum ClientState;
    {
        Created,
        Initializing,
        Ready,
        Authenticating,
        Disconnected,
        ConnectionError,
        ShuttingDown,
        Shutdown,
        Error;
    }

    public class ClientInfo;
    {
        public string ClientId { get; set; }
        public string Version { get; set; }
        public string Platform { get; set; }
        public DateTime StartTime { get; set; }
    }

    public class UserSession;
    {
        public string SessionId { get; set; }
        public string UserId { get; set; }
        public string Username { get; set; }
        public string AccessToken { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsValid => !string.IsNullOrEmpty(AccessToken) && ExpiresAt > DateTime.UtcNow;
    }

    public class ClientEvent;
    {
        public string EventId { get; set; }
        public ClientEventType EventType { get; set; }
        public string Message { get; set; }
        public DateTime Timestamp { get; set; }
        public string ClientId { get; set; }
        public string SessionId { get; set; }
    }

    public enum ClientEventType;
    {
        Initialized,
        Shutdown,
        UserLoggedIn,
        UserLoggedOut,
        ProjectCreated,
        ProjectUpdated,
        ProjectDeleted,
        CommandExecuted,
        ErrorOccurred;
    }

    // Simple performance monitor;
    public class PerformanceMonitor;
    {
        private readonly List<TimeSpan> _requestDurations;
        private readonly object _lockObject = new object();
        private int _totalRequests;
        private int _failedRequests;

        public PerformanceMonitor()
        {
            _requestDurations = new List<TimeSpan>();
        }

        public void RecordRequest(TimeSpan duration, System.Net.HttpStatusCode statusCode)
        {
            lock (_lockObject)
            {
                _totalRequests++;
                if (!IsSuccessStatusCode(statusCode))
                {
                    _failedRequests++;
                }
                _requestDurations.Add(duration);

                // Keep only last 1000 measurements;
                if (_requestDurations.Count > 1000)
                {
                    _requestDurations.RemoveAt(0);
                }
            }
        }

        public PerformanceMetrics GetCurrentMetrics()
        {
            lock (_lockObject)
            {
                var metrics = new PerformanceMetrics;
                {
                    TotalRequests = _totalRequests,
                    FailedRequests = _failedRequests,
                    SuccessRate = _totalRequests > 0 ? 1.0 - ((double)_failedRequests / _totalRequests) : 1.0;
                };

                if (_requestDurations.Count > 0)
                {
                    metrics.AverageResponseTime = TimeSpan.FromTicks(
                        (long)_requestDurations.Average(t => t.Ticks));
                    metrics.MaxResponseTime = _requestDurations.Max();
                    metrics.MinResponseTime = _requestDurations.Min();
                }

                return metrics;
            }
        }

        private bool IsSuccessStatusCode(System.Net.HttpStatusCode statusCode)
        {
            return ((int)statusCode) >= 200 && ((int)statusCode) < 300;
        }
    }
    #endregion;
}
