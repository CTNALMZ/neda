using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NEDA.API.ClientSDK;
using NEDA.API.Common;
using NEDA.API.DTOs;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.Core.Monitoring;
using NEDA.Core.Security;
using NEDA.Services.FileService;
using NEDA.Services.ProjectService;
using NEDA.Services.UserService;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NEDA.API.Controllers;
{
    /// <summary>
    /// Project management controller for NEDA system;
    /// Provides comprehensive project CRUD operations, file management, and collaboration features;
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    [Produces("application/json")]
    public class ProjectController : ControllerBase;
    {
        #region Private Fields;
        private readonly ILogger<ProjectController> _logger;
        private readonly IProjectManager _projectManager;
        private readonly IFileService _fileService;
        private readonly IUserManager _userManager;
        private readonly ISecurityManager _securityManager;
        private readonly IPerformanceMonitor _performanceMonitor;
        #endregion;

        #region Constructor;
        public ProjectController(
            ILogger<ProjectController> logger,
            IProjectManager projectManager,
            IFileService fileService,
            IUserManager userManager,
            ISecurityManager securityManager,
            IPerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _projectManager = projectManager ?? throw new ArgumentNullException(nameof(projectManager));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
        }
        #endregion;

        #region Project CRUD Operations;
        /// <summary>
        /// Get all projects for the current user with pagination and filtering;
        /// </summary>
        [HttpGet]
        [ProducesResponseType(typeof(ProjectListResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status401Unauthorized)]
        public async Task<ActionResult<ProjectListResponse>> GetProjects(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50,
            [FromQuery] string searchTerm = null,
            [FromQuery] ProjectStatus? status = null,
            [FromQuery] ProjectType? type = null,
            [FromQuery] string sortBy = "CreatedAt",
            [FromQuery] SortDirection sortDirection = SortDirection.Descending)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                // Validate pagination;
                if (page < 1) page = 1;
                if (pageSize < 1) pageSize = 1;
                if (pageSize > 1000) pageSize = 1000;

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to access projects",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check if user can view all projects;
                var canViewAll = await _securityManager.HasPermissionAsync(currentUserId, "ViewAllProjects");
                var targetUserId = canViewAll ? null : currentUserId;

                var filter = new ProjectFilter;
                {
                    UserId = targetUserId,
                    SearchTerm = searchTerm,
                    Status = status,
                    Type = type,
                    Page = page,
                    PageSize = pageSize,
                    SortBy = sortBy,
                    SortDirection = sortDirection;
                };

                var result = await _projectManager.GetProjectsAsync(filter);

                var response = new ProjectListResponse;
                {
                    Success = true,
                    Projects = result.Projects,
                    TotalCount = result.TotalCount,
                    Page = page,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling((double)result.TotalCount / pageSize),
                    Message = $"Retrieved {result.Projects.Count} projects",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Retrieved {ProjectCount} projects for user {UserId}",
                    result.Projects.Count, currentUserId);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving projects list");
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "PROJECT_LIST_FAILED",
                    Message = "Failed to retrieve projects",
                    Timestamp = DateTime.UtcNow,
                    Details = new { Page = page, PageSize = pageSize }
                });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _performanceMonitor.RecordApiCall(nameof(GetProjects), duration, true);
            }
        }

        /// <summary>
        /// Get a specific project by ID;
        /// </summary>
        [HttpGet("{projectId}")]
        [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<ProjectResponse>> GetProject(string projectId)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ID_REQUIRED",
                        Message = "Project ID is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to access project",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check project access;
                var hasAccess = await _projectManager.HasUserAccessAsync(projectId, currentUserId);
                if (!hasAccess)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ACCESS_DENIED",
                        Message = "Access to project denied",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var project = await _projectManager.GetProjectAsync(projectId);
                if (project == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_NOT_FOUND",
                        Message = $"Project not found: {projectId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var response = new ProjectResponse;
                {
                    Success = true,
                    Project = project,
                    Message = "Project retrieved successfully",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Retrieved project: {ProjectId} for user {UserId}", projectId, currentUserId);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving project: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "PROJECT_RETRIEVAL_FAILED",
                    Message = "Failed to retrieve project",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _performanceMonitor.RecordApiCall(nameof(GetProject), duration, true);
            }
        }

        /// <summary>
        /// Create a new project;
        /// </summary>
        [HttpPost]
        [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<ProjectResponse>> CreateProject([FromBody] CreateProjectRequest request)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (request == null)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "INVALID_REQUEST",
                        Message = "Project creation request cannot be null",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Validate request;
                var validationResult = ValidateCreateProjectRequest(request);
                if (!validationResult.IsValid)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "VALIDATION_FAILED",
                        Message = validationResult.ErrorMessage,
                        Timestamp = DateTime.UtcNow,
                        Details = validationResult.ValidationErrors;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to create projects",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check project creation permission;
                var canCreate = await _securityManager.HasPermissionAsync(currentUserId, "CreateProject");
                if (!canCreate)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_CREATION_DENIED",
                        Message = "Insufficient permissions to create projects",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var project = new Project;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = request.Name,
                    Description = request.Description,
                    Type = request.Type,
                    Settings = request.Settings ?? new ProjectSettings(),
                    CreatedBy = currentUserId,
                    CreatedAt = DateTime.UtcNow,
                    ModifiedAt = DateTime.UtcNow,
                    Status = ProjectStatus.Draft,
                    Version = 1;
                };

                var createdProject = await _projectManager.CreateProjectAsync(project, currentUserId);

                var response = new ProjectResponse;
                {
                    Success = true,
                    Project = createdProject,
                    Message = "Project created successfully",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Project created: {ProjectId} by user {UserId}", createdProject.Id, currentUserId);

                return CreatedAtAction(nameof(GetProject), new { projectId = createdProject.Id }, response);
            }
            catch (ProjectCreationException ex)
            {
                _logger.LogWarning(ex, "Project creation failed: {ProjectName}", request?.Name);
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "PROJECT_CREATION_FAILED",
                    Message = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating project");
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "PROJECT_CREATION_ERROR",
                    Message = "An unexpected error occurred while creating project",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _performanceMonitor.RecordApiCall(nameof(CreateProject), duration, true);
            }
        }

        /// <summary>
        /// Update an existing project;
        /// </summary>
        [HttpPut("{projectId}")]
        [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<ProjectResponse>> UpdateProject(string projectId, [FromBody] UpdateProjectRequest request)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ID_REQUIRED",
                        Message = "Project ID is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                if (request == null)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "INVALID_REQUEST",
                        Message = "Project update request cannot be null",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to update projects",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check project access and update permission;
                var canUpdate = await _projectManager.CanUserUpdateProjectAsync(projectId, currentUserId);
                if (!canUpdate)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_UPDATE_DENIED",
                        Message = "Insufficient permissions to update this project",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var existingProject = await _projectManager.GetProjectAsync(projectId);
                if (existingProject == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_NOT_FOUND",
                        Message = $"Project not found: {projectId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Apply updates;
                if (!string.IsNullOrWhiteSpace(request.Name))
                    existingProject.Name = request.Name;

                if (!string.IsNullOrWhiteSpace(request.Description))
                    existingProject.Description = request.Description;

                if (request.Settings != null)
                    existingProject.Settings = request.Settings;

                existingProject.ModifiedAt = DateTime.UtcNow;
                existingProject.ModifiedBy = currentUserId;

                var updatedProject = await _projectManager.UpdateProjectAsync(existingProject);

                var response = new ProjectResponse;
                {
                    Success = true,
                    Project = updatedProject,
                    Message = "Project updated successfully",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Project updated: {ProjectId} by user {UserId}", projectId, currentUserId);

                return Ok(response);
            }
            catch (ProjectUpdateException ex)
            {
                _logger.LogWarning(ex, "Project update failed: {ProjectId}", projectId);
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "PROJECT_UPDATE_FAILED",
                    Message = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating project: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "PROJECT_UPDATE_ERROR",
                    Message = "An unexpected error occurred while updating project",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _performanceMonitor.RecordApiCall(nameof(UpdateProject), duration, true);
            }
        }

        /// <summary>
        /// Delete a project;
        /// </summary>
        [HttpDelete("{projectId}")]
        [ProducesResponseType(typeof(OperationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
        public async Task<ActionResult<OperationResponse>> DeleteProject(string projectId)
        {
            var startTime = DateTime.UtcNow;

            try
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ID_REQUIRED",
                        Message = "Project ID is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to delete projects",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check project access and delete permission;
                var canDelete = await _projectManager.CanUserDeleteProjectAsync(projectId, currentUserId);
                if (!canDelete)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_DELETE_DENIED",
                        Message = "Insufficient permissions to delete this project",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var existingProject = await _projectManager.GetProjectAsync(projectId);
                if (existingProject == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_NOT_FOUND",
                        Message = $"Project not found: {projectId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                await _projectManager.DeleteProjectAsync(projectId);

                var response = new OperationResponse;
                {
                    Success = true,
                    Message = "Project deleted successfully",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Project deleted: {ProjectId} by user {UserId}", projectId, currentUserId);

                return Ok(response);
            }
            catch (ProjectDeleteException ex)
            {
                _logger.LogWarning(ex, "Project deletion failed: {ProjectId}", projectId);
                return BadRequest(new ErrorResponse;
                {
                    ErrorCode = "PROJECT_DELETE_FAILED",
                    Message = ex.Message,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting project: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "PROJECT_DELETE_ERROR",
                    Message = "An unexpected error occurred while deleting project",
                    Timestamp = DateTime.UtcNow;
                });
            }
            finally
            {
                var duration = DateTime.UtcNow - startTime;
                _performanceMonitor.RecordApiCall(nameof(DeleteProject), duration, true);
            }
        }
        #endregion;

        #region Project Operations;
        /// <summary>
        /// Change project status (Draft, Active, Archived, etc.)
        /// </summary>
        [HttpPost("{projectId}/status")]
        [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<ProjectResponse>> ChangeProjectStatus(string projectId, [FromBody] ChangeStatusRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId) || request == null)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "INVALID_REQUEST",
                        Message = "Project ID and status are required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to change project status",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check permission to change status;
                var canChangeStatus = await _projectManager.CanUserUpdateProjectAsync(projectId, currentUserId);
                if (!canChangeStatus)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "STATUS_CHANGE_DENIED",
                        Message = "Insufficient permissions to change project status",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var project = await _projectManager.GetProjectAsync(projectId);
                if (project == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_NOT_FOUND",
                        Message = $"Project not found: {projectId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Validate status transition;
                if (!IsValidStatusTransition(project.Status, request.NewStatus))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "INVALID_STATUS_TRANSITION",
                        Message = $"Cannot transition from {project.Status} to {request.NewStatus}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                project.Status = request.NewStatus;
                project.ModifiedAt = DateTime.UtcNow;
                project.ModifiedBy = currentUserId;

                var updatedProject = await _projectManager.UpdateProjectAsync(project);

                var response = new ProjectResponse;
                {
                    Success = true,
                    Project = updatedProject,
                    Message = $"Project status changed to {request.NewStatus}",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Project status changed: {ProjectId} -> {Status}", projectId, request.NewStatus);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error changing project status: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "STATUS_CHANGE_FAILED",
                    Message = "Failed to change project status",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Create a copy of an existing project;
        /// </summary>
        [HttpPost("{projectId}/copy")]
        [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status201Created)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<ProjectResponse>> CopyProject(string projectId, [FromBody] CopyProjectRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ID_REQUIRED",
                        Message = "Project ID is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to copy projects",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Check access to source project;
                var hasAccess = await _projectManager.HasUserAccessAsync(projectId, currentUserId);
                if (!hasAccess)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_COPY_DENIED",
                        Message = "Access to source project denied",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var sourceProject = await _projectManager.GetProjectAsync(projectId);
                if (sourceProject == null)
                {
                    return NotFound(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_NOT_FOUND",
                        Message = $"Source project not found: {projectId}",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var newProjectName = request.NewName ?? $"{sourceProject.Name} - Copy";

                var copiedProject = await _projectManager.CopyProjectAsync(projectId, newProjectName, currentUserId);

                var response = new ProjectResponse;
                {
                    Success = true,
                    Project = copiedProject,
                    Message = "Project copied successfully",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("Project copied: {SourceProjectId} -> {NewProjectId}", projectId, copiedProject.Id);

                return CreatedAtAction(nameof(GetProject), new { projectId = copiedProject.Id }, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error copying project: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "PROJECT_COPY_FAILED",
                    Message = "Failed to copy project",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Get project statistics and analytics;
        /// </summary>
        [HttpGet("{projectId}/analytics")]
        [ProducesResponseType(typeof(ProjectAnalyticsResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProjectAnalyticsResponse>> GetProjectAnalytics(string projectId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ID_REQUIRED",
                        Message = "Project ID is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                if (string.IsNullOrEmpty(currentUserId))
                {
                    return Unauthorized(new ErrorResponse;
                    {
                        ErrorCode = "AUTHENTICATION_REQUIRED",
                        Message = "Authentication required to view project analytics",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var hasAccess = await _projectManager.HasUserAccessAsync(projectId, currentUserId);
                if (!hasAccess)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "ANALYTICS_ACCESS_DENIED",
                        Message = "Access to project analytics denied",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var analytics = await _projectManager.GetProjectAnalyticsAsync(projectId);

                var response = new ProjectAnalyticsResponse;
                {
                    Success = true,
                    Analytics = analytics,
                    Message = "Project analytics retrieved successfully",
                    Timestamp = DateTime.UtcNow;
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving project analytics: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "ANALYTICS_RETRIEVAL_FAILED",
                    Message = "Failed to retrieve project analytics",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }
        #endregion;

        #region Project Collaboration;
        /// <summary>
        /// Get project members and their roles;
        /// </summary>
        [HttpGet("{projectId}/members")]
        [ProducesResponseType(typeof(ProjectMembersResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<ProjectMembersResponse>> GetProjectMembers(string projectId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "PROJECT_ID_REQUIRED",
                        Message = "Project ID is required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();
                var hasAccess = await _projectManager.HasUserAccessAsync(projectId, currentUserId);
                if (!hasAccess)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "MEMBERS_ACCESS_DENIED",
                        Message = "Access to project members denied",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var members = await _projectManager.GetProjectMembersAsync(projectId);

                var response = new ProjectMembersResponse;
                {
                    Success = true,
                    Members = members,
                    TotalCount = members.Count,
                    Message = "Project members retrieved successfully",
                    Timestamp = DateTime.UtcNow;
                };

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving project members: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "MEMBERS_RETRIEVAL_FAILED",
                    Message = "Failed to retrieve project members",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Add a member to the project;
        /// </summary>
        [HttpPost("{projectId}/members")]
        [ProducesResponseType(typeof(OperationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<OperationResponse>> AddProjectMember(string projectId, [FromBody] AddMemberRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId) || request == null)
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "INVALID_REQUEST",
                        Message = "Project ID and member data are required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();

                // Check if current user can manage project members;
                var canManageMembers = await _projectManager.CanUserManageMembersAsync(projectId, currentUserId);
                if (!canManageMembers)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "MEMBER_MANAGEMENT_DENIED",
                        Message = "Insufficient permissions to manage project members",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                await _projectManager.AddProjectMemberAsync(projectId, request.UserId, request.Role);

                var response = new OperationResponse;
                {
                    Success = true,
                    Message = $"User {request.UserId} added to project with role {request.Role}",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("User {UserId} added to project {ProjectId} with role {Role}",
                    request.UserId, projectId, request.Role);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding project member: {ProjectId}", projectId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "MEMBER_ADD_FAILED",
                    Message = "Failed to add project member",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }

        /// <summary>
        /// Remove a member from the project;
        /// </summary>
        [HttpDelete("{projectId}/members/{userId}")]
        [ProducesResponseType(typeof(OperationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status403Forbidden)]
        public async Task<ActionResult<OperationResponse>> RemoveProjectMember(string projectId, string userId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(userId))
                {
                    return BadRequest(new ErrorResponse;
                    {
                        ErrorCode = "INVALID_REQUEST",
                        Message = "Project ID and User ID are required",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                var currentUserId = GetCurrentUserId();

                // Check if current user can manage project members;
                var canManageMembers = await _projectManager.CanUserManageMembersAsync(projectId, currentUserId);
                if (!canManageMembers)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new ErrorResponse;
                    {
                        ErrorCode = "MEMBER_MANAGEMENT_DENIED",
                        Message = "Insufficient permissions to manage project members",
                        Timestamp = DateTime.UtcNow;
                    });
                }

                // Prevent users from removing themselves if they're the only admin;
                if (userId == currentUserId)
                {
                    var isLastAdmin = await _projectManager.IsLastAdminAsync(projectId, userId);
                    if (isLastAdmin)
                    {
                        return BadRequest(new ErrorResponse;
                        {
                            ErrorCode = "CANNOT_REMOVE_LAST_ADMIN",
                            Message = "Cannot remove the last admin from project",
                            Timestamp = DateTime.UtcNow;
                        });
                    }
                }

                await _projectManager.RemoveProjectMemberAsync(projectId, userId);

                var response = new OperationResponse;
                {
                    Success = true,
                    Message = $"User {userId} removed from project",
                    Timestamp = DateTime.UtcNow;
                };

                _logger.LogInformation("User {UserId} removed from project {ProjectId}", userId, projectId);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing project member: {ProjectId}, {UserId}", projectId, userId);
                return StatusCode(StatusCodes.Status500InternalServerError, new ErrorResponse;
                {
                    ErrorCode = "MEMBER_REMOVE_FAILED",
                    Message = "Failed to remove project member",
                    Timestamp = DateTime.UtcNow;
                });
            }
        }
        #endregion;

        #region Private Methods;
        private string GetCurrentUserId()
        {
            // Extract user ID from claims (simplified implementation)
            return User?.FindFirst("userId")?.Value ??
                   User?.FindFirst("sub")?.Value ??
                   "anonymous";
        }

        private ValidationResult ValidateCreateProjectRequest(CreateProjectRequest request)
        {
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(request.Name))
            {
                errors.Add("Project name is required");
            }
            else if (request.Name.Length > 100)
            {
                errors.Add("Project name cannot exceed 100 characters");
            }

            if (!string.IsNullOrWhiteSpace(request.Description) && request.Description.Length > 1000)
            {
                errors.Add("Project description cannot exceed 1000 characters");
            }

            return new ValidationResult;
            {
                IsValid = errors.Count == 0,
                ErrorMessage = errors.Count > 0 ? "Validation failed" : null,
                ValidationErrors = errors;
            };
        }

        private bool IsValidStatusTransition(ProjectStatus currentStatus, ProjectStatus newStatus)
        {
            var allowedTransitions = new Dictionary<ProjectStatus, ProjectStatus[]>
            {
                [ProjectStatus.Draft] = new[] { ProjectStatus.Active, ProjectStatus.Archived },
                [ProjectStatus.Active] = new[] { ProjectStatus.Paused, ProjectStatus.Archived, ProjectStatus.Completed },
                [ProjectStatus.Paused] = new[] { ProjectStatus.Active, ProjectStatus.Archived },
                [ProjectStatus.Completed] = new[] { ProjectStatus.Archived },
                [ProjectStatus.Archived] = new[] { ProjectStatus.Active }
            };

            return allowedTransitions.ContainsKey(currentStatus) &&
                   allowedTransitions[currentStatus].Contains(newStatus);
        }
        #endregion;

        #region Supporting Classes;
        private class ValidationResult;
        {
            public bool IsValid { get; set; }
            public string ErrorMessage { get; set; }
            public List<string> ValidationErrors { get; set; } = new List<string>();
        }
        #endregion;
    }
}
