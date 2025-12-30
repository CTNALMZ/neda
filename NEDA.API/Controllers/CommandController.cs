using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Commands;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Engine;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security;
using System.Threading.Tasks;

namespace NEDA.API.Controllers;
{
    /// <summary>
    /// Command execution and management controller for NEDA system;
    /// Provides endpoints for executing commands, checking status, and managing command history;
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class CommandController : ControllerBase;
    {
        #region Private Fields;
        private readonly ILogger<CommandController> _logger;
        private readonly ICommandExecutor _commandExecutor;
        private readonly ICommandRegistry _commandRegistry
        private readonly ISecurityManager _securityManager;
        private readonly ISystemManager _systemManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ICommandHistoryService _commandHistoryService;
        #endregion;

        #region Constructor;
        public CommandController(
            ILogger<CommandController> logger,
            ICommandExecutor commandExecutor,
            ICommandRegistry commandRegistry,
            ISecurityManager securityManager,
            ISystemManager systemManager,
            IPerformanceMonitor performanceMonitor,
            ICommandHistoryService commandHistoryService)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
            _commandRegistry = commandRegistry ?? throw new ArgumentNullException(nameof(commandRegistry));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _commandHistoryService = commandHistoryService ?? throw new ArgumentNullException(nameof(commandHistoryService));
        }
        #endregion;

        #region Command Execution;
        /// <summary>
        /// Execute a command in the NEDA system;
        /// </summary>
        /// <param name="request">Command execution request</param>
        /// <returns>Command execution result</returns>
        [HttpPost("execute")]
        [ProducesResponseType(typeof(CommandResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<CommandResponse>> ExecuteCommand([FromBody] CommandRequest request)
        {
            var startTime = DateTime.UtcNow;
            string commandId = null;

            try
            {
                // Validate request;
                if (request == null)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_INVALID_REQUEST",
                        Message = "Command request cannot be null",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                if (string.IsNullOrWhiteSpace(request.CommandName))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_MISSING_NAME",
                        Message = "Command name is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Authenticate and authorize;
                var authResult = await AuthenticateAndAuthorizeAsync(request);
                if (!authResult.IsAuthorized)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_UNAUTHORIZED",
                        Message = authResult.ErrorMessage,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Validate command exists and is enabled;
                if (!_commandRegistry.IsCommandRegistered(request.CommandName))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_NOT_FOUND",
                        Message = $"Command '{request.CommandName}' is not registered",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                if (!_commandRegistry.IsCommandEnabled(request.CommandName))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_DISABLED",
                        Message = $"Command '{request.CommandName}' is currently disabled",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check system resources before execution;
                var resourceCheck = await CheckSystemResourcesAsync();
                if (!resourceCheck.IsSufficient)
                {
                    return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse;
                    {
                        ErrorCode = "SYSTEM_RESOURCES_INSUFFICIENT",
                        Message = resourceCheck.ErrorMessage,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Execute command;
                var executionContext = new CommandExecutionContext;
                {
                    CommandName = request.CommandName,
                    Parameters = request.Parameters ?? new Dictionary<string, object>(),
                    UserId = authResult.UserId,
                    SessionId = request.SessionId,
                    ClientId = request.ClientId,
                    RequestId = Guid.NewGuid().ToString(),
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Executing command: {CommandName} for user: {UserId}",
                    request.CommandName, authResult.UserId);

                var result = await _commandExecutor.ExecuteCommandAsync(executionContext);
                commandId = result.CommandId;

                // Record command execution;
                await _commandHistoryService.RecordCommandExecutionAsync(executionContext, result);

                // Update performance metrics;
                _performanceMonitor.RecordCommandExecution(request.CommandName, result.ExecutionTime);

                var response = new CommandResponse;
                {
                    Success = result.Success,
                    CommandId = result.CommandId,
                    Result = result.Result,
                    Message = result.Message,
                    ExecutionTime = result.ExecutionTime,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Command executed successfully: {CommandName} (ID: {CommandId})",
                    request.CommandName, result.CommandId);

                return Ok(response);
            }
            catch (SecurityException ex)
            {
                _logger.LogWarning(ex, "Security violation in command execution: {CommandName}", request?.CommandName);

                return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                {
                    ErrorCode = "SECURITY_VIOLATION",
                    Message = ex.Message,
                    Timestamp = DateTime.UtcNow,
                    Details = new { CommandName = request?.CommandName }
                });
            }
            catch (SystemResourceException ex)
            {
                _logger.LogWarning(ex, "System resource issue in command execution: {CommandName}", request?.CommandName);

                return StatusCode(StatusCodes.Status503ServiceUnavailable, new ErrorResponse;
                {
                    ErrorCode = "SYSTEM_RESOURCE_ERROR",
                    Message = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error executing command: {CommandName}", request?.CommandName);

                // Record failed execution;
                if (commandId != null)
                {
                    await _commandHistoryService.RecordCommandFailureAsync(commandId, ex);
                }

                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "COMMAND_EXECUTION_FAILED",
                    Message = "An unexpected error occurred during command execution",
                    Timestamp = DateTime.UtcNow,
                    Details = new { OriginalError = ex.Message }
                });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _logger.LogDebug("Command execution completed in {Duration}ms", duration.TotalMilliseconds);
            }
        }

        /// <summary>
        /// Execute multiple commands in batch;
        /// </summary>
        [HttpPost("execute-batch")]
        [ProducesResponseType(typeof(BatchCommandResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<BatchCommandResponse>> ExecuteBatchCommands([FromBody] BatchCommandRequest request)
        {
            if (request == null || request.Commands == null || !request.Commands.Any())
            {
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "BATCH_COMMAND_INVALID_REQUEST",
                    Message = "Batch command request cannot be null or empty",
                    Timestamp = DateTime.UtcNow;
                });
            }

            if (request.Commands.Count > 100) // Limit batch size;
            {
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "BATCH_COMMAND_TOO_LARGE",
                    Message = "Batch command limit exceeded (max 100 commands)",
                    Timestamp = DateTime.UtcNow;
                });
            }

            var results = new List<CommandResponse>();
            var failedCount = 0;

            foreach (var commandRequest in request.Commands)
            {
                try
                {
                    var result = await ExecuteCommandInternalAsync(commandRequest);
                    results.Add(result);

                    if (!result.Success)
                    {
                        failedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute batch command: {CommandName}", commandRequest.CommandName);
                    failedCount++;

                    results.Add(new CommandResponse;
                    {
                        Success = false,
                        CommandId = null,
                        Message = $"Execution failed: {ex.Message}",
                        ExecutionTime = TimeSpan.Zero,
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }

            return Ok(new BatchCommandResponse;
            {
                TotalCommands = request.Commands.Count,
                SuccessfulCommands = request.Commands.Count - failedCount,
                FailedCommands = failedCount,
                Results = results,
                Timestamp = DateTime.UtcNow;
            });
        }
        #endregion;

        #region Command Status and History;
        /// <summary>
        /// Get the status of a specific command execution;
        /// </summary>
        [HttpGet("{commandId}/status")]
        [ProducesResponseType(typeof(CommandStatusResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<CommandStatusResponse>> GetCommandStatus(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "COMMAND_ID_REQUIRED",
                    Message = "Command ID is required",
                    Timestamp = DateTime.UtcNow;
                });
            }

            try
            {
                var executionRecord = await _commandHistoryService.GetCommandExecutionAsync(commandId);
                if (executionRecord == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_NOT_FOUND",
                        Message = $"Command execution record not found: {commandId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check authorization - users can only see their own commands unless they have admin rights;
                var authResult = await CheckCommandAccessAsync(executionRecord.UserId);
                if (!authResult.IsAuthorized)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "ACCESS_DENIED",
                        Message = "Access to command execution record denied",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var statusResponse = new CommandStatusResponse;
                {
                    CommandId = executionRecord.CommandId,
                    CommandName = executionRecord.CommandName,
                    Status = executionRecord.Status,
                    Progress = executionRecord.Progress,
                    Result = executionRecord.Result,
                    Message = executionRecord.Message,
                    StartTime = executionRecord.StartTime,
                    EndTime = executionRecord.EndTime,
                    ExecutionTime = executionRecord.ExecutionTime,
                    UserId = executionRecord.UserId;
                };

                return Ok(statusResponse);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving command status: {CommandId}", commandId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "STATUS_RETRIEVAL_FAILED",
                    Message = "Failed to retrieve command status",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Get command execution history with filtering and pagination;
        /// </summary>
        [HttpGet("history")]
        [ProducesResponseType(typeof(CommandHistoryResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<CommandHistoryResponse>> GetCommandHistory(
            [FromQuery] string userId = null,
            [FromQuery] string commandName = null,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null,
            [FromQuery] CommandStatus? status = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            try
            {
                // Validate pagination;
                if (page < 1) page = 1;
                if (pageSize < 1) pageSize = 1;
                if (pageSize > 1000) pageSize = 1000;

                // Check if user has permission to view other users' history;
                var currentUserId = GetCurrentUserId();
                var canViewAll = await _securityManager.HasPermissionAsync(currentUserId, "ViewAllCommandHistory");

                if (!canViewAll && !string.IsNullOrEmpty(userId) && userId != currentUserId)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "HISTORY_ACCESS_DENIED",
                        Message = "Insufficient permissions to view other users' command history",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // If user can't view all, only show their own history;
                if (!canViewAll)
                {
                    userId = currentUserId;
                }

                var filter = new CommandHistoryFilter;
                {
                    UserId = userId,
                    CommandName = commandName,
                    FromDate = fromDate,
                    ToDate = toDate,
                    Status = status,
                    Page = page,
                    PageSize = pageSize;
                };

                var history = await _commandHistoryService.GetCommandHistoryAsync(filter);

                var response = new CommandHistoryResponse;
                {
                    Commands = history.Commands,
                    TotalCount = history.TotalCount,
                    Page = page,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling((double)history.TotalCount / pageSize),
                    Timestamp = DateTime.UtcNow;
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving command history");
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "HISTORY_RETRIEVAL_FAILED",
                    Message = "Failed to retrieve command history",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Cancel a running command execution;
        /// </summary>
        [HttpPost("{commandId}/cancel")]
        [ProducesResponseType(typeof(OperationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<OperationResponse>> CancelCommand(string commandId)
        {
            if (string.IsNullOrWhiteSpace(commandId))
            {
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "COMMAND_ID_REQUIRED",
                    Message = "Command ID is required",
                    Timestamp = DateTime.UtcNow;
                });
            }

            try
            {
                var executionRecord = await _commandHistoryService.GetCommandExecutionAsync(commandId);
                if (executionRecord == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_NOT_FOUND",
                        Message = $"Command execution record not found: {commandId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check if command is still running;
                if (executionRecord.Status != CommandStatus.Running)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_NOT_RUNNING",
                        Message = $"Command is not running (current status: {executionRecord.Status})",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check authorization;
                var currentUserId = GetCurrentUserId();
                var canCancel = await _securityManager.HasPermissionAsync(currentUserId, "CancelAnyCommand") ||
                               executionRecord.UserId == currentUserId;

                if (!canCancel)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "CANCEL_PERMISSION_DENIED",
                        Message = "Insufficient permissions to cancel this command",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var success = await _commandExecutor.CancelCommandAsync(commandId);

                if (success)
                {
                    await _commandHistoryService.UpdateCommandStatusAsync(commandId, CommandStatus.Cancelled, "Command cancelled by user");

                    _logger.LogInformation("Command cancelled: {CommandId} by user: {UserId}", commandId, currentUserId);

                    return Ok(new OperationResponse;
                    {
                        Success = true,
                        Message = "Command cancelled successfully",
                        Timestamp = DateTime.UtcNow;
                    });
                }
                else;
                {
                    return BadRequest(new OperationResponse;
                    {
                        Success = false,
                        Message = "Failed to cancel command",
                        Timestamp = DateTime.UtcNow;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cancelling command: {CommandId}", commandId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "CANCEL_FAILED",
                    Message = "Failed to cancel command",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }
        #endregion;

        #region Command Information;
        /// <summary>
        /// Get list of all available commands;
        /// </summary>
        [HttpGet("available")]
        [ProducesResponseType(typeof(AvailableCommandsResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<AvailableCommandsResponse>> GetAvailableCommands()
        {
            try
            {
                var currentUserId = GetCurrentUserId();
                var commands = _commandRegistry.GetAllCommands();

                // Filter commands based on user permissions;
                var availableCommands = new List<CommandInfo>();

                foreach (var command in commands)
                {
                    if (await _securityManager.HasPermissionAsync(currentUserId, $"Execute:{command.Name}"))
                    {
                        availableCommands.Add(new CommandInfo;
                        {
                            Name = command.Name,
                            Description = command.Description,
                            Category = command.Category,
                            IsEnabled = command.IsEnabled,
                            RequiredPermissions = command.RequiredPermissions,
                            Parameters = command.Parameters,
                            EstimatedDuration = command.EstimatedDuration;
                        });
                    }
                }

                var response = new AvailableCommandsResponse;
                {
                    Commands = availableCommands,
                    TotalCount = availableCommands.Count,
                    Timestamp = DateTime.UtcNow;
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving available commands");
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "COMMAND_LIST_FAILED",
                    Message = "Failed to retrieve available commands",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Get detailed information about a specific command;
        /// </summary>
        [HttpGet("info/{commandName}")]
        [ProducesResponseType(typeof(CommandInfoResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<CommandInfoResponse>> GetCommandInfo(string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "COMMAND_NAME_REQUIRED",
                    Message = "Command name is required",
                    Timestamp = DateTime.UtcNow;
                });
            }

            try
            {
                var command = _commandRegistry.GetCommand(commandName);
                if (command == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_NOT_FOUND",
                        Message = $"Command not found: {commandName}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                var hasPermission = await _securityManager.HasPermissionAsync(currentUserId, $"Execute:{command.Name}");

                if (!hasPermission)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "COMMAND_ACCESS_DENIED",
                        Message = "Insufficient permissions to view command information",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var response = new CommandInfoResponse;
                {
                    Command = new CommandInfo;
                    {
                        Name = command.Name,
                        Description = command.Description,
                        Category = command.Category,
                        IsEnabled = command.IsEnabled,
                        RequiredPermissions = command.RequiredPermissions,
                        Parameters = command.Parameters,
                        EstimatedDuration = command.EstimatedDuration;
                    },
                    Timestamp = DateTime.UtcNow;
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving command info: {CommandName}", commandName);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "COMMAND_INFO_FAILED",
                    Message = "Failed to retrieve command information",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }
        #endregion;

        #region Private Methods;
        private async Task<AuthorizationResult> AuthenticateAndAuthorizeAsync(CommandRequest request)
        {
            // Extract authentication token from request headers;
            var authHeader = Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader))
            {
                return AuthorizationResult.Failed("Authentication required");
            }

            // Validate token and get user info;
            var userInfo = await _securityManager.ValidateTokenAsync(authHeader);
            if (userInfo == null || !userInfo.IsAuthenticated)
            {
                return AuthorizationResult.Failed("Invalid or expired authentication token");
            }

            // Check command execution permission;
            var hasPermission = await _securityManager.HasPermissionAsync(
                userInfo.UserId, $"Execute:{request.CommandName}");

            if (!hasPermission)
            {
                return AuthorizationResult.Failed($"Insufficient permissions to execute command: {request.CommandName}");
            }

            return AuthorizationResult.Success(userInfo.UserId);
        }

        private async Task<ResourceCheckResult> CheckSystemResourcesAsync()
        {
            var systemStatus = await _systemManager.GetSystemStatusAsync();

            if (systemStatus.CpuUsage > 90)
            {
                return ResourceCheckResult.Insufficient("CPU usage too high");
            }

            if (systemStatus.MemoryUsage > 85)
            {
                return ResourceCheckResult.Insufficient("Memory usage too high");
            }

            if (systemStatus.DiskUsage > 95)
            {
                return ResourceCheckResult.Insufficient("Disk space low");
            }

            return ResourceCheckResult.Sufficient();
        }

        private async Task<AuthorizationResult> CheckCommandAccessAsync(string commandUserId)
        {
            var currentUserId = GetCurrentUserId();

            if (currentUserId == commandUserId)
            {
                return AuthorizationResult.Success(currentUserId);
            }

            var canViewAll = await _securityManager.HasPermissionAsync(currentUserId, "ViewAllCommandHistory");
            if (canViewAll)
            {
                return AuthorizationResult.Success(currentUserId);
            }

            return AuthorizationResult.Failed("Access to command execution record denied");
        }

        private string GetCurrentUserId()
        {
            // Extract user ID from claims (simplified)
            return User?.FindFirst("userId")?.Value ?? "anonymous";
        }

        private async Task<CommandResponse> ExecuteCommandInternalAsync(CommandRequest request)
        {
            // Simplified internal execution for batch processing;
            var executionContext = new CommandExecutionContext;
            {
                CommandName = request.CommandName,
                Parameters = request.Parameters ?? new Dictionary<string, object>(),
                UserId = GetCurrentUserId(),
                SessionId = request.SessionId,
                ClientId = request.ClientId,
                RequestId = Guid.NewGuid().ToString(),
                Timestamp = DateTime.UtcNow;
            };

            var result = await _commandExecutor.ExecuteCommandAsync(executionContext);
            await _commandHistoryService.RecordCommandExecutionAsync(executionContext, result);

            return new CommandResponse;
            {
                Success = result.Success,
                CommandId = result.CommandId,
                Result = result.Result,
                Message = result.Message,
                ExecutionTime = result.ExecutionTime,
                Timestamp = DateTime.UtcNow;
            };
        }
        #endregion;

        #region Supporting Classes;
        private class AuthorizationResult;
        {
            public bool IsAuthorized { get; set; }
            public string UserId { get; set; }
            public string ErrorMessage { get; set; }

            public static AuthorizationResult Success(string userId) => new AuthorizationResult;
            {
                IsAuthorized = true,
                UserId = userId;
            };

            public static AuthorizationResult Failed(string errorMessage) => new AuthorizationResult;
            {
                IsAuthorized = false,
                ErrorMessage = errorMessage;
            };
        }

        private class ResourceCheckResult;
        {
            public bool IsSufficient { get; set; }
            public string ErrorMessage { get; set; }

            public static ResourceCheckResult Sufficient() => new ResourceCheckResult;
            {
                IsSufficient = true;
            };

            public static ResourceCheckResult Insufficient(string errorMessage) => new ResourceCheckResult;
            {
                IsSufficient = false,
                ErrorMessage = errorMessage;
            };
        }
        #endregion;
    }
}
