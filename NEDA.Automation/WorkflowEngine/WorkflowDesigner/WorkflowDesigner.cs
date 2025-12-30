using NEDA.API.Middleware;
using NEDA.Automation.Executors;
using NEDA.Automation.WorkflowEngine.ActivityLibrary;
using NEDA.Automation.WorkflowEngine.ExecutionEngine;
using NEDA.Common;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.EngineIntegration.Unreal.Physics;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.Services.Messaging.EventBus;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using static NEDA.API.Middleware.LoggingMiddleware;

namespace NEDA.Automation.WorkflowEngine.WorkflowDesigner;
{
    /// <summary>
    /// Görsel Workflow Tasarım Motoru;
    /// Tasarım desenleri: MVVM, Observer, Command, Composite;
    /// </summary>
    public class WorkflowDesigner : INotifyPropertyChanged, IDisposable;
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IActivityLibrary _activityLibrary;
        private readonly WorkflowEngine _workflowEngine;
        private readonly IErrorReporter _errorReporter;
        private readonly IPerformanceMonitor _performanceMonitor;

        private WorkflowProject _currentProject;
        private WorkflowCanvas _designCanvas;
        private ActivityBase _selectedActivity;
        private WorkflowValidationResult _validationResult;
        private bool _isModified;
        private bool _isLoading;
        private DesignerMode _currentMode;
        private readonly UndoRedoManager _undoRedoManager;
        private readonly ZoomManager _zoomManager;
        private readonly GridManager _gridManager;
        private readonly ConnectionManager _connectionManager;
        private readonly PropertyInspector _propertyInspector;
        private readonly CompilationService _compilationService;
        private readonly List<WorkflowError> _designErrors;
        private readonly Dictionary<string, DesignerTool> _availableTools;
        private DesignerTool _activeTool;
        private WorkflowSimulation _simulation;

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif workflow projesi;
        /// </summary>
        public WorkflowProject CurrentProject;
        {
            get => _currentProject;
            private set;
            {
                if (_currentProject != value)
                {
                    _currentProject = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ProjectName));
                    OnPropertyChanged(nameof(HasProject));
                }
            }
        }

        /// <summary>
        /// Proje adı;
        /// </summary>
        public string ProjectName => CurrentProject?.Name ?? "Untitled";

        /// <summary>
        /// Aktif proje var mı?
        /// </summary>
        public bool HasProject => CurrentProject != null;

        /// <summary>
        Seçili aktivite;
        /// </summary>
        public ActivityBase SelectedActivity;
        {
            get => _selectedActivity;
            set;
            {
                if (_selectedActivity != value)
                {
                    _selectedActivity = value;
                    OnPropertyChanged();
                    ShowPropertyInspector();
                }
            }
        }

        /// <summary>
        /// Tasarım modu;
        /// </summary>
        public DesignerMode CurrentMode;
        {
            get => _currentMode;
            set;
            {
                if (_currentMode != value)
                {
                    _currentMode = value;
                    OnPropertyChanged();
                    OnModeChanged();
                }
            }
        }

        /// <summary>
        /// Değişiklik yapıldı mı?
        /// </summary>
        public bool IsModified;
        {
            get => _isModified;
            private set;
            {
                if (_isModified != value)
                {
                    _isModified = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WindowTitle));
                }
            }
        }

        /// <summary>
        /// Pencere başlığı;
        /// </summary>
        public string WindowTitle => $"{ProjectName}{(IsModified ? "*" : "")} - Workflow Designer";

        /// <summary>
        /// Doğrulama sonuçları;
        /// </summary>
        public WorkflowValidationResult ValidationResult;
        {
            get => _validationResult;
            private set;
            {
                _validationResult = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Tasarım hataları;
        /// </summary>
        public IReadOnlyList<WorkflowError> DesignErrors => _designErrors;

        /// <summary>
        /// Kullanılabilir tasarım araçları;
        /// </summary>
        public IReadOnlyDictionary<string, DesignerTool> AvailableTools => _availableTools;

        /// <summary>
        /// Aktif tasarım aracı;
        /// </summary>
        public DesignerTool ActiveTool;
        {
            get => _activeTool;
            set;
            {
                if (_activeTool != value)
                {
                    _activeTool?.Deactivate();
                    _activeTool = value;
                    _activeTool?.Activate(this);
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// Grid görünür mü?
        /// </summary>
        public bool IsGridVisible;
        {
            get => _gridManager.IsVisible;
            set;
            {
                _gridManager.IsVisible = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Snap to grid aktif mi?
        /// </summary>
        public bool IsSnapToGridEnabled;
        {
            get => _gridManager.SnapToGrid;
            set;
            {
                _gridManager.SnapToGrid = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Zoom seviyesi;
        /// </summary>
        public double ZoomLevel;
        {
            get => _zoomManager.ZoomLevel;
            set => _zoomManager.ZoomLevel = value;
        }

        #endregion;

        #region Events;

        /// <summary>
        /// Property değişiklik event'i;
        /// </summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// Workflow değişti event'i;
        /// </summary>
        public event EventHandler<WorkflowChangedEventArgs> WorkflowChanged;

        /// <summary>
        /// Aktivite seçildi event'i;
        /// </summary>
        public event EventHandler<ActivitySelectedEventArgs> ActivitySelected;

        /// <summary>
        /// Tasarım doğrulandı event'i;
        /// </summary>
        public event EventHandler<DesignValidatedEventArgs> DesignValidated;

        /// <summary>
        /// Simülasyon durumu değişti event'i;
        /// </summary>
        public event EventHandler<SimulationStateChangedEventArgs> SimulationStateChanged;

        #endregion;

        #region Constructor;

        /// <summary>
        /// WorkflowDesigner constructor;
        /// </summary>
        public WorkflowDesigner(
            ILogger logger,
            IEventBus eventBus,
            IActivityLibrary activityLibrary,
            WorkflowEngine workflowEngine,
            IErrorReporter errorReporter,
            IPerformanceMonitor performanceMonitor)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _activityLibrary = activityLibrary ?? throw new ArgumentNullException(nameof(activityLibrary));
            _workflowEngine = workflowEngine ?? throw new ArgumentNullException(nameof(workflowEngine));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));

            // Manager'ları initialize et;
            _undoRedoManager = new UndoRedoManager();
            _zoomManager = new ZoomManager();
            _gridManager = new GridManager();
            _connectionManager = new ConnectionManager();
            _propertyInspector = new PropertyInspector();
            _compilationService = new CompilationService();

            _designErrors = new List<WorkflowError>();
            _availableTools = InitializeDesignTools();
            _simulation = new WorkflowSimulation();

            // Varsayılan mod;
            CurrentMode = DesignerMode.Design;

            // Event subscription;
            SubscribeToEvents();

            _logger.LogInformation("WorkflowDesigner initialized successfully", GetType());
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Yeni workflow projesi oluştur;
        /// </summary>
        public async Task<WorkflowProject> CreateNewProjectAsync(string projectName, WorkflowTemplate template = null)
        {
            try
            {
                _performanceMonitor.StartOperation("CreateNewProject");
                _logger.LogDebug($"Creating new project: {projectName}");

                // Mevcut projeyi kontrol et;
                if (IsModified)
                {
                    var saveResult = await RequestSaveChangesAsync();
                    if (saveResult == SaveConfirmationResult.Cancel)
                    {
                        return null;
                    }
                }

                // Yeni proje oluştur;
                var project = template != null;
                    ? WorkflowProject.FromTemplate(projectName, template)
                    : new WorkflowProject(projectName);

                // Projeyi yükle;
                await LoadProjectAsync(project);

                _logger.LogInformation($"New project created: {projectName}");
                _eventBus.Publish(new WorkflowProjectCreatedEvent(project));

                return project;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create new project");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.CreateProjectFailed);
                throw new WorkflowDesignerException("Failed to create new project", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("CreateNewProject");
            }
        }

        /// <summary>
        /// Proje yükle;
        /// </summary>
        public async Task LoadProjectAsync(WorkflowProject project)
        {
            if (project == null)
                throw new ArgumentNullException(nameof(project));

            try
            {
                _isLoading = true;
                _performanceMonitor.StartOperation("LoadProject");
                _logger.LogDebug($"Loading project: {project.Name}");

                // Canvas'ı temizle;
                ClearDesignCanvas();

                // Projeyi yükle;
                CurrentProject = project;

                // Workflow'u canvas'a yükle;
                await LoadWorkflowToCanvasAsync(project.Workflow);

                // Doğrulama yap;
                await ValidateDesignAsync();

                IsModified = false;

                _logger.LogInformation($"Project loaded successfully: {project.Name}");
                _eventBus.Publish(new WorkflowProjectLoadedEvent(project));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load project");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.LoadProjectFailed);
                throw new WorkflowDesignerException("Failed to load project", ex);
            }
            finally
            {
                _isLoading = false;
                _performanceMonitor.EndOperation("LoadProject");
            }
        }

        /// <summary>
        /// Projeyi kaydet;
        /// </summary>
        public async Task<bool> SaveProjectAsync(string filePath = null)
        {
            try
            {
                if (CurrentProject == null)
                    return false;

                _performanceMonitor.StartOperation("SaveProject");
                _logger.LogDebug($"Saving project: {CurrentProject.Name}");

                // Doğrulama yap;
                var validationResult = await ValidateDesignAsync();
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning($"Cannot save project with validation errors: {validationResult.ErrorCount} errors");
                    return false;
                }

                // Proje bilgilerini güncelle;
                CurrentProject.LastModified = DateTime.UtcNow;
                CurrentProject.Workflow = GetWorkflowFromCanvas();

                // Kaydet;
                var success = await CurrentProject.SaveAsync(filePath);

                if (success)
                {
                    IsModified = false;
                    _logger.LogInformation($"Project saved successfully: {CurrentProject.Name}");
                    _eventBus.Publish(new WorkflowProjectSavedEvent(CurrentProject));
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save project");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.SaveProjectFailed);
                throw new WorkflowDesignerException("Failed to save project", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("SaveProject");
            }
        }

        /// <summary>
        /// Aktivite ekle;
        /// </summary>
        public void AddActivity(ActivityBase activity, Point position)
        {
            try
            {
                if (activity == null)
                    throw new ArgumentNullException(nameof(activity));

                _performanceMonitor.StartOperation("AddActivity");
                _logger.LogDebug($"Adding activity: {activity.Name} at position {position}");

                // Grid snap ayarı;
                if (IsSnapToGridEnabled)
                {
                    position = _gridManager.SnapToGrid(position);
                }

                // Aktiviteyi canvas'a ekle;
                var canvasActivity = new CanvasActivity(activity, position);
                _designCanvas.AddActivity(canvasActivity);

                // Undo kaydı oluştur;
                _undoRedoManager.Record(new AddActivityCommand(this, canvasActivity));

                // Seçili aktiviteyi ayarla;
                SelectedActivity = activity;

                // Değişiklik işaretle;
                MarkAsModified();

                _logger.LogDebug($"Activity added successfully: {activity.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add activity");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.AddActivityFailed);
                throw new WorkflowDesignerException("Failed to add activity", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AddActivity");
            }
        }

        /// <summary>
        /// Bağlantı oluştur;
        /// </summary>
        public void CreateConnection(CanvasActivity sourceActivity, string outputPort,
                                   CanvasActivity targetActivity, string inputPort)
        {
            try
            {
                _performanceMonitor.StartOperation("CreateConnection");
                _logger.LogDebug($"Creating connection from {sourceActivity.Name} to {targetActivity.Name}");

                // Bağlantıyı oluştur;
                var connection = _connectionManager.CreateConnection(
                    sourceActivity, outputPort,
                    targetActivity, inputPort);

                // Canvas'a ekle;
                _designCanvas.AddConnection(connection);

                // Undo kaydı oluştur;
                _undoRedoManager.Record(new CreateConnectionCommand(this, connection));

                // Değişiklik işaretle;
                MarkAsModified();

                _logger.LogDebug($"Connection created successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create connection");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.CreateConnectionFailed);
                throw new WorkflowDesignerException("Failed to create connection", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("CreateConnection");
            }
        }

        /// <summary>
        /// Tasarımı doğrula;
        /// </summary>
        public async Task<WorkflowValidationResult> ValidateDesignAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("ValidateDesign");
                _logger.LogDebug("Validating workflow design");

                var workflow = GetWorkflowFromCanvas();
                var validator = new WorkflowValidator();

                // Doğrulama yap;
                ValidationResult = await validator.ValidateAsync(workflow);

                // Hataları güncelle;
                _designErrors.Clear();
                _designErrors.AddRange(ValidationResult.Errors);

                // Event publish;
                DesignValidated?.Invoke(this, new DesignValidatedEventArgs(ValidationResult));

                _logger.LogInformation($"Design validation completed: {ValidationResult.ErrorCount} errors");

                return ValidationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Validation failed");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.ValidationFailed);
                throw new WorkflowDesignerException("Validation failed", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("ValidateDesign");
            }
        }

        /// <summary>
        /// Workflow'u derle;
        /// </summary>
        public async Task<CompilationResult> CompileWorkflowAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("CompileWorkflow");
                _logger.LogDebug("Compiling workflow");

                // Önce doğrulama yap;
                var validationResult = await ValidateDesignAsync();
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Cannot compile invalid workflow");
                    return CompilationResult.FromValidationResult(validationResult);
                }

                // Workflow'u al;
                var workflow = GetWorkflowFromCanvas();

                // Derle;
                var result = await _compilationService.CompileAsync(workflow);

                if (result.Success)
                {
                    _logger.LogInformation("Workflow compiled successfully");
                    _eventBus.Publish(new WorkflowCompiledEvent(workflow, result));
                }
                else;
                {
                    _logger.LogError($"Compilation failed: {result.ErrorMessage}");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Compilation failed");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.CompilationFailed);
                throw new WorkflowDesignerException("Compilation failed", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("CompileWorkflow");
            }
        }

        /// <summary>
        /// Simülasyon başlat;
        /// </summary>
        public async Task StartSimulationAsync()
        {
            try
            {
                if (CurrentMode == DesignerMode.Simulation)
                    return;

                _performanceMonitor.StartOperation("StartSimulation");
                _logger.LogDebug("Starting workflow simulation");

                // Doğrulama yap;
                var validationResult = await ValidateDesignAsync();
                if (!validationResult.IsValid)
                {
                    _logger.LogWarning("Cannot simulate invalid workflow");
                    return;
                }

                // Modu değiştir;
                CurrentMode = DesignerMode.Simulation;

                // Simülasyonu başlat;
                var workflow = GetWorkflowFromCanvas();
                await _simulation.StartAsync(workflow, _workflowEngine);

                _logger.LogInformation("Simulation started successfully");
                SimulationStateChanged?.Invoke(this,
                    new SimulationStateChangedEventArgs(SimulationState.Running));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start simulation");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.StartSimulationFailed);
                throw new WorkflowDesignerException("Failed to start simulation", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StartSimulation");
            }
        }

        /// <summary>
        /// Simülasyonu durdur;
        /// </summary>
        public void StopSimulation()
        {
            try
            {
                if (CurrentMode != DesignerMode.Simulation)
                    return;

                _performanceMonitor.StartOperation("StopSimulation");
                _logger.LogDebug("Stopping workflow simulation");

                // Simülasyonu durdur;
                _simulation.Stop();

                // Modu değiştir;
                CurrentMode = DesignerMode.Design;

                _logger.LogInformation("Simulation stopped");
                SimulationStateChanged?.Invoke(this,
                    new SimulationStateChangedEventArgs(SimulationState.Stopped));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop simulation");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.StopSimulationFailed);
                throw new WorkflowDesignerException("Failed to stop simulation", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("StopSimulation");
            }
        }

        /// <summary>
        /// Undo yap;
        /// </summary>
        public void Undo()
        {
            try
            {
                if (!_undoRedoManager.CanUndo)
                    return;

                _performanceMonitor.StartOperation("Undo");
                _logger.LogDebug("Performing undo operation");

                _undoRedoManager.Undo();
                MarkAsModified();

                _logger.LogDebug("Undo completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Undo failed");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.UndoFailed);
                throw new WorkflowDesignerException("Undo failed", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("Undo");
            }
        }

        /// <summary>
        /// Redo yap;
        /// </summary>
        public void Redo()
        {
            try
            {
                if (!_undoRedoManager.CanRedo)
                    return;

                _performanceMonitor.StartOperation("Redo");
                _logger.LogDebug("Performing redo operation");

                _undoRedoManager.Redo();
                MarkAsModified();

                _logger.LogDebug("Redo completed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redo failed");
                _errorReporter.ReportError(ex, ErrorCodes.WorkflowDesigner.RedoFailed);
                throw new WorkflowDesignerException("Redo failed", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("Redo");
            }
        }

        /// <summary>
        /// Zoom in;
        /// </summary>
        public void ZoomIn()
        {
            _zoomManager.ZoomIn();
            OnPropertyChanged(nameof(ZoomLevel));
        }

        /// <summary>
        /// Zoom out;
        /// </summary>
        public void ZoomOut()
        {
            _zoomManager.ZoomOut();
            OnPropertyChanged(nameof(ZoomLevel));
        }

        /// <summary>
        /// Zoom reset;
        /// </summary>
        public void ZoomReset()
        {
            _zoomManager.Reset();
            OnPropertyChanged(nameof(ZoomLevel));
        }

        /// <summary>
        /// Tasarım araçlarını getir;
        /// </summary>
        public IEnumerable<DesignerTool> GetToolsByCategory(ToolCategory category)
        {
            return _availableTools.Values;
                .Where(t => t.Category == category)
                .OrderBy(t => t.Order);
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Tasarım araçlarını başlat;
        /// </summary>
        private Dictionary<string, DesignerTool> InitializeDesignTools()
        {
            var tools = new Dictionary<string, DesignerTool>
            {
                ["Select"] = new SelectionTool(),
                ["Pan"] = new PanTool(),
                ["Activity"] = new ActivityTool(),
                ["Connection"] = new ConnectionTool(),
                ["Text"] = new TextTool(),
                ["Shape"] = new ShapeTool(),
                ["Comment"] = new CommentTool(),
                ["Lasso"] = new LassoSelectionTool(),
                ["Alignment"] = new AlignmentTool(),
                ["Distribution"] = new DistributionTool()
            };

            return tools;
        }

        /// <summary>
        /// Event'lara subscribe ol;
        /// </summary>
        private void SubscribeToEvents()
        {
            _undoRedoManager.StateChanged += OnUndoRedoStateChanged;
            _zoomManager.ZoomChanged += OnZoomChanged;
            _gridManager.GridChanged += OnGridChanged;
            _connectionManager.ConnectionCreated += OnConnectionCreated;
            _simulation.StateChanged += OnSimulationStateChanged;

            // Property inspector events;
            _propertyInspector.PropertyValueChanged += OnPropertyValueChanged;
        }

        /// <summary>
        /// Canvas'ı temizle;
        /// </summary>
        private void ClearDesignCanvas()
        {
            _designCanvas?.Clear();
            _designErrors.Clear();
            SelectedActivity = null;
            _undoRedoManager.Clear();
        }

        /// <summary>
        /// Workflow'u canvas'a yükle;
        /// </summary>
        private async Task LoadWorkflowToCanvasAsync(Workflow workflow)
        {
            if (workflow == null || workflow.Activities == null)
                return;

            _designCanvas = new WorkflowCanvas();

            // Aktivite'leri yükle;
            foreach (var activity in workflow.Activities)
            {
                var canvasActivity = new CanvasActivity(activity, activity.Position);
                _designCanvas.AddActivity(canvasActivity);
            }

            // Bağlantıları yükle;
            foreach (var connection in workflow.Connections)
            {
                var canvasConnection = _connectionManager.CreateConnectionFromModel(connection, _designCanvas);
                _designCanvas.AddConnection(canvasConnection);
            }

            await Task.CompletedTask;
        }

        /// <summary>
        /// Canvas'dan workflow al;
        /// </summary>
        private Workflow GetWorkflowFromCanvas()
        {
            if (_designCanvas == null)
                return null;

            var workflow = new Workflow(CurrentProject?.Name ?? "Untitled");

            // Aktivite'leri ekle;
            foreach (var canvasActivity in _designCanvas.Activities)
            {
                var activity = canvasActivity.Activity;
                activity.Position = canvasActivity.Position;
                workflow.AddActivity(activity);
            }

            // Bağlantıları ekle;
            foreach (var connection in _designCanvas.Connections)
            {
                workflow.AddConnection(connection.ToWorkflowConnection());
            }

            return workflow;
        }

        /// <summary>
        /// Property inspector göster;
        /// </summary>
        private void ShowPropertyInspector()
        {
            if (_selectedActivity != null)
            {
                _propertyInspector.Inspect(_selectedActivity);
            }
        }

        /// <summary>
        /// Değişiklik işaretle;
        /// </summary>
        private void MarkAsModified()
        {
            if (!_isLoading)
            {
                IsModified = true;
                WorkflowChanged?.Invoke(this, new WorkflowChangedEventArgs(ChangeType.Modified));
            }
        }

        /// <summary>
        /// Kaydetme onayı iste;
        /// </summary>
        private async Task<SaveConfirmationResult> RequestSaveChangesAsync()
        {
            // UI katmanına event gönder;
            var eventArgs = new SaveConfirmationRequestedEventArgs(ProjectName);
            _eventBus.Publish(eventArgs);

            // Event'ın handle edilmesini bekle (UI katmanı modal gösterir)
            await Task.Delay(100); // UI yanıtı için kısa bekleme;

            // Bu noktada UI katmanı kullanıcıdan yanıt alır ve event ile geri bildirir;
            // Şimdilik varsayılan değer döndür;
            return SaveConfirmationResult.Save;
        }

        #endregion;

        #region Event Handlers;

        private void OnUndoRedoStateChanged(object sender, UndoRedoStateEventArgs e)
        {
            OnPropertyChanged(nameof(_undoRedoManager.CanUndo));
            OnPropertyChanged(nameof(_undoRedoManager.CanRedo));
        }

        private void OnZoomChanged(object sender, ZoomChangedEventArgs e)
        {
            OnPropertyChanged(nameof(ZoomLevel));
        }

        private void OnGridChanged(object sender, GridChangedEventArgs e)
        {
            OnPropertyChanged(nameof(IsGridVisible));
            OnPropertyChanged(nameof(IsSnapToGridEnabled));
        }

        private void OnConnectionCreated(object sender, ConnectionCreatedEventArgs e)
        {
            _logger.LogDebug($"Connection created: {e.Connection}");
        }

        private void OnSimulationStateChanged(object sender, SimulationStateChangedEventArgs e)
        {
            SimulationStateChanged?.Invoke(this, e);
        }

        private void OnPropertyValueChanged(object sender, PropertyValueChangedEventArgs e)
        {
            MarkAsModified();
            _logger.LogDebug($"Property changed: {e.PropertyName} = {e.NewValue}");
        }

        private void OnModeChanged()
        {
            _logger.LogDebug($"Designer mode changed to: {CurrentMode}");
            _eventBus.Publish(new DesignerModeChangedEvent(CurrentMode));
        }

        #endregion;

        #region INotifyPropertyChanged Implementation;

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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
                    // Managed kaynakları temizle;
                    _simulation?.Dispose();
                    _propertyInspector?.Dispose();
                    _connectionManager?.Dispose();

                    // Event subscription'ları temizle;
                    if (_undoRedoManager != null)
                        _undoRedoManager.StateChanged -= OnUndoRedoStateChanged;

                    if (_zoomManager != null)
                        _zoomManager.ZoomChanged -= OnZoomChanged;

                    if (_gridManager != null)
                        _gridManager.GridChanged -= OnGridChanged;

                    if (_connectionManager != null)
                        _connectionManager.ConnectionCreated -= OnConnectionCreated;

                    if (_simulation != null)
                        _simulation.StateChanged -= OnSimulationStateChanged;

                    if (_propertyInspector != null)
                        _propertyInspector.PropertyValueChanged -= OnPropertyValueChanged;
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

        #region Nested Types;

        /// <summary>
        /// Tasarım modları;
        /// </summary>
        public enum DesignerMode;
        {
            Design,
            Simulation,
            Debug,
            Presentation;
        }

        /// <summary>
        /// Tasarım aracı kategorileri;
        /// </summary>
        public enum ToolCategory;
        {
            Selection,
            Creation,
            Modification,
            Annotation,
            Layout;
        }

        /// <summary>
        /// Kaydetme onayı sonuçları;
        /// </summary>
        public enum SaveConfirmationResult;
        {
            Save,
            DontSave,
            Cancel;
        }

        /// <summary>
        /// Değişiklik türleri;
        /// </summary>
        public enum ChangeType;
        {
            Modified,
            StructureChanged,
            PropertyChanged,
            ValidationChanged;
        }

        #endregion;
    }

    #region Supporting Classes;

    /// <summary>
    /// Workflow tasarım canvas'ı;
    /// </summary>
    public class WorkflowCanvas;
    {
        private readonly List<CanvasActivity> _activities;
        private readonly List<CanvasConnection> _connections;
        private readonly List<DesignElement> _elements;

        public WorkflowCanvas()
        {
            _activities = new List<CanvasActivity>();
            _connections = new List<CanvasConnection>();
            _elements = new List<DesignElement>();
        }

        public IReadOnlyList<CanvasActivity> Activities => _activities;
        public IReadOnlyList<CanvasConnection> Connections => _connections;
        public IReadOnlyList<DesignElement> Elements => _elements;

        public void AddActivity(CanvasActivity activity)
        {
            _activities.Add(activity);
            _elements.Add(activity);
        }

        public void AddConnection(CanvasConnection connection)
        {
            _connections.Add(connection);
            _elements.Add(connection);
        }

        public void RemoveActivity(CanvasActivity activity)
        {
            _activities.Remove(activity);
            _elements.Remove(activity);
        }

        public void RemoveConnection(CanvasConnection connection)
        {
            _connections.Remove(connection);
            _elements.Remove(connection);
        }

        public void Clear()
        {
            _activities.Clear();
            _connections.Clear();
            _elements.Clear();
        }
    }

    /// <summary>
    /// Canvas aktivite'si;
    /// </summary>
    public class CanvasActivity : DesignElement;
    {
        public ActivityBase Activity { get; }
        public Point Position { get; set; }
        public Size Size { get; set; }
        public List<Port> InputPorts { get; }
        public List<Port> OutputPorts { get; }

        public CanvasActivity(ActivityBase activity, Point position)
        {
            Activity = activity ?? throw new ArgumentNullException(nameof(activity));
            Position = position;
            Size = new Size(120, 80);
            InputPorts = new List<Port>();
            OutputPorts = new List<Port>();
            InitializePorts();
        }

        private void InitializePorts()
        {
            // Input port'ları oluştur;
            foreach (var input in Activity.Inputs)
            {
                InputPorts.Add(new Port(input.Name, PortType.Input, this));
            }

            // Output port'ları oluştur;
            foreach (var output in Activity.Outputs)
            {
                OutputPorts.Add(new Port(output.Name, PortType.Output, this));
            }
        }

        public string Name => Activity.Name;
        public Rect Bounds => new Rect(Position, Size);
    }

    /// <summary>
    /// Canvas bağlantısı;
    /// </summary>
    public class CanvasConnection : DesignElement;
    {
        public CanvasActivity SourceActivity { get; }
        public Port SourcePort { get; }
        public CanvasActivity TargetActivity { get; }
        public Port TargetPort { get; }
        public List<Point> Points { get; }

        public CanvasConnection(CanvasActivity sourceActivity, Port sourcePort,
                              CanvasActivity targetActivity, Port targetPort)
        {
            SourceActivity = sourceActivity ?? throw new ArgumentNullException(nameof(sourceActivity));
            SourcePort = sourcePort ?? throw new ArgumentNullException(nameof(sourcePort));
            TargetActivity = targetActivity ?? throw new ArgumentNullException(nameof(targetActivity));
            TargetPort = targetPort ?? throw new ArgumentNullException(nameof(targetPort));
            Points = new List<Point>();
            CalculatePath();
        }

        private void CalculatePath()
        {
            // Bağlantı yolunu hesapla (bezier curve veya orthogonal)
            var start = SourcePort.Position;
            var end = TargetPort.Position;

            // Basit başlangıç için düz çizgi;
            Points.Add(start);
            Points.Add(end);
        }

        public WorkflowConnection ToWorkflowConnection()
        {
            return new WorkflowConnection(
                SourceActivity.Activity.Id,
                SourcePort.Name,
                TargetActivity.Activity.Id,
                TargetPort.Name);
        }
    }

    /// <summary>
    /// Tasarım elemanı base class;
    /// </summary>
    public abstract class DesignElement;
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsSelected { get; set; }
        public bool IsVisible { get; set; } = true;
        public double ZIndex { get; set; }
    }

    /// <summary>
    /// Port bilgisi;
    /// </summary>
    public class Port;
    {
        public string Name { get; }
        public PortType Type { get; }
        public CanvasActivity Activity { get; }
        public Point Position { get; set; }

        public Port(string name, PortType type, CanvasActivity activity)
        {
            Name = name;
            Type = type;
            Activity = activity;
        }
    }

    /// <summary>
    /// Port türleri;
    /// </summary>
    public enum PortType;
    {
        Input,
        Output,
        InOut;
    }

    #endregion;

    #region Event Args Classes;

    public class WorkflowChangedEventArgs : EventArgs;
    {
        public ChangeType ChangeType { get; }
        public DateTime Timestamp { get; }

        public WorkflowChangedEventArgs(ChangeType changeType)
        {
            ChangeType = changeType;
            Timestamp = DateTime.UtcNow;
        }
    }

    public class ActivitySelectedEventArgs : EventArgs;
    {
        public ActivityBase Activity { get; }
        public Point Position { get; }

        public ActivitySelectedEventArgs(ActivityBase activity, Point position)
        {
            Activity = activity;
            Position = position;
        }
    }

    public class DesignValidatedEventArgs : EventArgs;
    {
        public WorkflowValidationResult ValidationResult { get; }

        public DesignValidatedEventArgs(WorkflowValidationResult validationResult)
        {
            ValidationResult = validationResult;
        }
    }

    public class SimulationStateChangedEventArgs : EventArgs;
    {
        public SimulationState State { get; }

        public SimulationStateChangedEventArgs(SimulationState state)
        {
            State = state;
        }
    }

    public class SaveConfirmationRequestedEventArgs : EventArgs;
    {
        public string ProjectName { get; }

        public SaveConfirmationRequestedEventArgs(string projectName)
        {
            ProjectName = projectName;
        }
    }

    #endregion;
}
