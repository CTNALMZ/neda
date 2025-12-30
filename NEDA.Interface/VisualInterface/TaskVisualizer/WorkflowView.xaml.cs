using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NEDA.Interface.VisualInterface.TaskVisualizer.ViewModels;
using NEDA.Common.Exceptions;
using NEDA.Logging;

namespace NEDA.Interface.VisualInterface.TaskVisualizer;
{
    /// <summary>
    /// Interaction logic for WorkflowView.xaml;
    /// </summary>
    public partial class WorkflowView : UserControl;
    {
        private WorkflowViewModel _viewModel;
        private Point _lastMousePosition;
        private bool _isPanning;
        private bool _isSelecting;

        public WorkflowView()
        {
            try
            {
                InitializeComponent();
                Loaded += OnLoaded;
                Unloaded += OnUnloaded;

                // Set up keyboard navigation;
                Focusable = true;
                Loaded += (s, e) => Focus();
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Failed to initialize WorkflowView: {ex.Message}", ex);
                throw new WorkflowInitializationException("Failed to initialize Workflow View", ex);
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _viewModel = DataContext as WorkflowViewModel;
                if (_viewModel != null)
                {
                    _viewModel.ViewLoaded();
                    HookEvents();
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error loading WorkflowView: {ex.Message}", ex);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                UnhookEvents();

                if (_viewModel != null)
                {
                    _viewModel.ViewUnloaded();
                    _viewModel = null;
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error unloading WorkflowView: {ex.Message}", ex);
            }
        }

        private void HookEvents()
        {
            MainScrollViewer.PreviewMouseWheel += OnPreviewMouseWheel;
            MainScrollViewer.PreviewMouseDown += OnCanvasMouseDown;
            MainScrollViewer.PreviewMouseUp += OnCanvasMouseUp;
            MainScrollViewer.PreviewMouseMove += OnCanvasMouseMove;
            MainScrollViewer.PreviewKeyDown += OnPreviewKeyDown;

            // Handle right-click for context menu;
            MainScrollViewer.ContextMenuOpening += OnContextMenuOpening;
        }

        private void UnhookEvents()
        {
            MainScrollViewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            MainScrollViewer.PreviewMouseDown -= OnCanvasMouseDown;
            MainScrollViewer.PreviewMouseUp -= OnCanvasMouseUp;
            MainScrollViewer.PreviewMouseMove -= OnCanvasMouseMove;
            MainScrollViewer.PreviewKeyDown -= OnPreviewKeyDown;
            MainScrollViewer.ContextMenuOpening -= OnContextMenuOpening;
        }

        #region Event Handlers;

        private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            try
            {
                if (Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Zoom with Ctrl + Mouse Wheel;
                    var zoomDelta = e.Delta > 0 ? 0.1 : -0.1;
                    _viewModel?.AdjustZoom(zoomDelta);
                    e.Handled = true;
                }
                else if (Keyboard.Modifiers == ModifierKeys.Shift)
                {
                    // Horizontal scroll with Shift + Mouse Wheel;
                    MainScrollViewer.ScrollToHorizontalOffset(
                        MainScrollViewer.HorizontalOffset - e.Delta);
                    e.Handled = true;
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error handling mouse wheel: {ex.Message}", ex);
            }
        }

        private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                _lastMousePosition = e.GetPosition(MainScrollViewer);

                if (e.ChangedButton == MouseButton.Middle ||
                    (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.Space))
                {
                    // Start panning;
                    _isPanning = true;
                    MainScrollViewer.Cursor = Cursors.Hand;
                    MainScrollViewer.CaptureMouse();
                    e.Handled = true;
                }
                else if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    // Start rectangle selection;
                    _isSelecting = true;
                    StartSelectionRectangle(_lastMousePosition);
                    MainScrollViewer.CaptureMouse();
                    e.Handled = true;
                }

                _viewModel?.CanvasMouseDown(e.GetPosition(CanvasContainer), e);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error handling canvas mouse down: {ex.Message}", ex);
            }
        }

        private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isPanning)
                {
                    _isPanning = false;
                    MainScrollViewer.Cursor = Cursors.Arrow;
                    MainScrollViewer.ReleaseMouseCapture();
                }

                if (_isSelecting)
                {
                    _isSelecting = false;
                    EndSelectionRectangle();
                    MainScrollViewer.ReleaseMouseCapture();
                }

                _viewModel?.CanvasMouseUp(e.GetPosition(CanvasContainer), e);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error handling canvas mouse up: {ex.Message}", ex);
            }
        }

        private void OnCanvasMouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                var currentPosition = e.GetPosition(MainScrollViewer);

                if (_isPanning)
                {
                    // Pan the canvas;
                    var delta = _lastMousePosition - currentPosition;
                    MainScrollViewer.ScrollToHorizontalOffset(
                        MainScrollViewer.HorizontalOffset + delta.X);
                    MainScrollViewer.ScrollToVerticalOffset(
                        MainScrollViewer.VerticalOffset + delta.Y);
                    _lastMousePosition = currentPosition;
                }
                else if (_isSelecting)
                {
                    // Update selection rectangle;
                    UpdateSelectionRectangle(currentPosition);
                }
                else;
                {
                    // Update cursor position for ViewModel;
                    var canvasPosition = e.GetPosition(CanvasContainer);
                    _viewModel?.UpdateCursorPosition(canvasPosition);
                }

                _viewModel?.CanvasMouseMove(e.GetPosition(CanvasContainer), e);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error handling canvas mouse move: {ex.Message}", ex);
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                switch (e.Key)
                {
                    case Key.Add:
                    case Key.OemPlus:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.ZoomInCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.Subtract:
                    case Key.OemMinus:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.ZoomOutCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.D0:
                    case Key.NumPad0:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.ResetZoomCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.Delete:
                        _viewModel?.DeleteSelectedNodesCommand.Execute(null);
                        e.Handled = true;
                        break;

                    case Key.A:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.SelectAllCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.C:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.CopySelectedNodesCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.V:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.PasteCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.Z:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            _viewModel?.UndoCommand.Execute(null);
                            e.Handled = true;
                        }
                        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
                        {
                            _viewModel?.RedoCommand.Execute(null);
                            e.Handled = true;
                        }
                        break;

                    case Key.F:
                        if (Keyboard.Modifiers == ModifierKeys.Control)
                        {
                            SearchBox.Focus();
                            e.Handled = true;
                        }
                        break;

                    case Key.Escape:
                        _viewModel?.CancelCurrentOperationCommand.Execute(null);
                        e.Handled = true;
                        break;
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error handling key down: {ex.Message}", ex);
            }
        }

        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            try
            {
                var position = Mouse.GetPosition(CanvasContainer);
                var hitTest = VisualTreeHelper.HitTest(CanvasContainer, position);

                if (hitTest != null)
                {
                    var node = FindParentNode(hitTest.VisualHit);
                    if (node != null)
                    {
                        // Show node context menu;
                        node.ContextMenu = FindResource("NodeContextMenu") as ContextMenu;
                        _viewModel?.SetSelectedNode(node.Tag?.ToString());
                    }
                    else;
                    {
                        // Show canvas context menu;
                        CanvasContainer.ContextMenu = FindResource("CanvasContextMenu") as ContextMenu;
                        _viewModel?.ClearSelection();
                    }
                }
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error opening context menu: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Selection Rectangle Methods;

        private void StartSelectionRectangle(Point startPoint)
        {
            try
            {
                SelectionRect.Visibility = Visibility.Visible;

                var canvasPoint = MainScrollViewer.TranslatePoint(startPoint, CanvasContainer);
                Canvas.SetLeft(SelectionRect, canvasPoint.X);
                Canvas.SetTop(SelectionRect, canvasPoint.Y);
                SelectionRect.Width = 0;
                SelectionRect.Height = 0;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error starting selection rectangle: {ex.Message}", ex);
            }
        }

        private void UpdateSelectionRectangle(Point currentPoint)
        {
            try
            {
                var startPoint = _lastMousePosition;
                var canvasStart = MainScrollViewer.TranslatePoint(startPoint, CanvasContainer);
                var canvasCurrent = MainScrollViewer.TranslatePoint(currentPoint, CanvasContainer);

                var left = System.Math.Min(canvasStart.X, canvasCurrent.X);
                var top = System.Math.Min(canvasStart.Y, canvasCurrent.Y);
                var width = System.Math.Abs(canvasCurrent.X - canvasStart.X);
                var height = System.Math.Abs(canvasCurrent.Y - canvasStart.Y);

                Canvas.SetLeft(SelectionRect, left);
                Canvas.SetTop(SelectionRect, top);
                SelectionRect.Width = width;
                SelectionRect.Height = height;

                // Select nodes within rectangle;
                _viewModel?.SelectNodesInRectangle(new Rect(left, top, width, height));
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error updating selection rectangle: {ex.Message}", ex);
            }
        }

        private void EndSelectionRectangle()
        {
            try
            {
                SelectionRect.Visibility = Visibility.Collapsed;
                SelectionRect.Width = 0;
                SelectionRect.Height = 0;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error ending selection rectangle: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Helper Methods;

        private ContentControl FindParentNode(DependencyObject visual)
        {
            try
            {
                while (visual != null && !(visual is ContentControl))
                {
                    visual = VisualTreeHelper.GetParent(visual);
                }

                return visual as ContentControl;
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error finding parent node: {ex.Message}", ex);
                return null;
            }
        }

        public void ZoomToFit()
        {
            try
            {
                _viewModel?.ZoomToFitCommand.Execute(null);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error zooming to fit: {ex.Message}", ex);
            }
        }

        public void CenterOnNode(string nodeId)
        {
            try
            {
                _viewModel?.CenterOnNode(nodeId);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error centering on node: {ex.Message}", ex);
            }
        }

        public void RefreshView()
        {
            try
            {
                NodesContainer.Items.Refresh();
                _viewModel?.RefreshConnections();
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error refreshing view: {ex.Message}", ex);
            }
        }

        #endregion;

        #region Public Methods for External Access;

        public void LoadWorkflow(string workflowId)
        {
            try
            {
                _viewModel?.LoadWorkflow(workflowId);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error loading workflow: {ex.Message}", ex);
                throw;
            }
        }

        public void SaveWorkflow()
        {
            try
            {
                _viewModel?.SaveWorkflowCommand.Execute(null);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error saving workflow: {ex.Message}", ex);
                throw;
            }
        }

        public void ExportToImage(string filePath)
        {
            try
            {
                _viewModel?.ExportToImage(filePath);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error exporting to image: {ex.Message}", ex);
                throw;
            }
        }

        public void PrintWorkflow()
        {
            try
            {
                _viewModel?.PrintWorkflowCommand.Execute(null);
            }
            catch (System.Exception ex)
            {
                Logger.Error($"Error printing workflow: {ex.Message}", ex);
                throw;
            }
        }

        #endregion;
    }

    /// <summary>
    /// Custom exception for workflow view errors;
    /// </summary>
    public class WorkflowViewException : System.Exception;
    {
        public WorkflowViewException(string message) : base(message) { }
        public WorkflowViewException(string message, System.Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// Exception for workflow initialization errors;
    /// </summary>
    public class WorkflowInitializationException : WorkflowViewException;
    {
        public WorkflowInitializationException(string message) : base(message) { }
        public WorkflowInitializationException(string message, System.Exception inner) : base(message, inner) { }
    }
}
