using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Collections.ObjectModel;

namespace NEDA.CharacterSystems.AI_Behaviors.AnimationBlueprints;
{
    /// <summary>
    /// Graph node for animation blueprints;
    /// </summary>
    public class GraphNode : INotifyPropertyChanged;
    {
        private string _nodeId;
        private string _nodeName;
        private double _x;
        private double _y;
        private NodeType _nodeType;
        private object _nodeData;

        public string NodeId;
        {
            get => _nodeId;
            set { _nodeId = value; OnPropertyChanged(); }
        }

        public string NodeName;
        {
            get => _nodeName;
            set { _nodeName = value; OnPropertyChanged(); }
        }

        public double X;
        {
            get => _x;
            set { _x = value; OnPropertyChanged(); }
        }

        public double Y;
        {
            get => _y;
            set { _y = value; OnPropertyChanged(); }
        }

        public NodeType NodeType;
        {
            get => _nodeType;
            set { _nodeType = value; OnPropertyChanged(); }
        }

        public object NodeData;
        {
            get => _nodeData;
            set { _nodeData = value; OnPropertyChanged(); }
        }

        public ObservableCollection<NodeConnection> Connections { get; } = new ObservableCollection<NodeConnection>();

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Connection between graph nodes;
    /// </summary>
    public class NodeConnection : INotifyPropertyChanged;
    {
        private string _connectionId;
        private string _sourceNodeId;
        private string _targetNodeId;
        private string _outputPort;
        private string _inputPort;
        private ConnectionType _connectionType;

        public string ConnectionId;
        {
            get => _connectionId;
            set { _connectionId = value; OnPropertyChanged(); }
        }

        public string SourceNodeId;
        {
            get => _sourceNodeId;
            set { _sourceNodeId = value; OnPropertyChanged(); }
        }

        public string TargetNodeId;
        {
            get => _targetNodeId;
            set { _targetNodeId = value; OnPropertyChanged(); }
        }

        public string OutputPort;
        {
            get => _outputPort;
            set { _outputPort = value; OnPropertyChanged(); }
        }

        public string InputPort;
        {
            get => _inputPort;
            set { _inputPort = value; OnPropertyChanged(); }
        }

        public ConnectionType ConnectionType;
        {
            get => _connectionType;
            set { _connectionType = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Graph view model for animation blueprints;
    /// </summary>
    public class GraphViewModel : INotifyPropertyChanged;
    {
        private GraphNode _selectedNode;
        private NodeConnection _selectedConnection;
        private double _zoomLevel = 1.0;
        private double _panX;
        private double _panY;
        private bool _isSnapToGridEnabled = true;

        public ObservableCollection<GraphNode> Nodes { get; } = new ObservableCollection<GraphNode>();
        public ObservableCollection<NodeConnection> Connections { get; } = new ObservableCollection<NodeConnection>();

        public GraphNode SelectedNode;
        {
            get => _selectedNode;
            set { _selectedNode = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedNode)); }
        }

        public NodeConnection SelectedConnection;
        {
            get => _selectedConnection;
            set { _selectedConnection = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSelectedConnection)); }
        }

        public double ZoomLevel;
        {
            get => _zoomLevel;
            set { _zoomLevel = Math.Max(0.1, Math.Min(3.0, value)); OnPropertyChanged(); }
        }

        public double PanX;
        {
            get => _panX;
            set { _panX = value; OnPropertyChanged(); }
        }

        public double PanY;
        {
            get => _panY;
            set { _panY = value; OnPropertyChanged(); }
        }

        public bool IsSnapToGridEnabled;
        {
            get => _isSnapToGridEnabled;
            set { _isSnapToGridEnabled = value; OnPropertyChanged(); }
        }

        public bool HasSelectedNode => SelectedNode != null;
        public bool HasSelectedConnection => SelectedConnection != null;

        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Main graph editor for animation blueprints;
    /// </summary>
    public class GraphEditor;
    {
        private readonly GraphViewModel _viewModel;
        private readonly IAnimationGraphCompiler _compiler;
        private readonly IGraphValidator _validator;
        private readonly Stack<IGraphCommand> _undoStack;
        private readonly Stack<IGraphCommand> _redoStack;

        public GraphViewModel ViewModel => _viewModel;

        public event EventHandler<GraphModifiedEventArgs> GraphModified;
        public event EventHandler<GraphValidationEventArgs> GraphValidated;
        public event EventHandler<GraphCompilationEventArgs> GraphCompiled;

        public GraphEditor(IAnimationGraphCompiler compiler, IGraphValidator validator)
        {
            _viewModel = new GraphViewModel();
            _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
            _validator = validator ?? throw new ArgumentNullException(nameof(validator));
            _undoStack = new Stack<IGraphCommand>();
            _redoStack = new Stack<IGraphCommand>();
        }

        /// <summary>
        /// Creates a new node in the graph;
        /// </summary>
        public GraphNode CreateNode(NodeType nodeType, double x, double y, string nodeName = null)
        {
            var node = new GraphNode;
            {
                NodeId = Guid.NewGuid().ToString(),
                NodeName = nodeName ?? GetDefaultNodeName(nodeType),
                NodeType = nodeType,
                X = _viewModel.IsSnapToGridEnabled ? SnapToGrid(x) : x,
                Y = _viewModel.IsSnapToGridEnabled ? SnapToGrid(y) : y;
            };

            var command = new AddNodeCommand(_viewModel, node);
            ExecuteCommand(command);

            return node;
        }

        /// <summary>
        /// Deletes the selected node;
        /// </summary>
        public void DeleteSelectedNode()
        {
            if (_viewModel.SelectedNode == null) return;

            var node = _viewModel.SelectedNode;
            var connections = _viewModel.Connections;
                .Where(c => c.SourceNodeId == node.NodeId || c.TargetNodeId == node.NodeId)
                .ToList();

            var command = new DeleteNodeCommand(_viewModel, node, connections);
            ExecuteCommand(command);
        }

        /// <summary>
        /// Creates a connection between two nodes;
        /// </summary>
        public NodeConnection CreateConnection(string sourceNodeId, string targetNodeId,
            string outputPort, string inputPort, ConnectionType connectionType = ConnectionType.Execution)
        {
            var sourceNode = _viewModel.Nodes.FirstOrDefault(n => n.NodeId == sourceNodeId);
            var targetNode = _viewModel.Nodes.FirstOrDefault(n => n.NodeId == targetNodeId);

            if (sourceNode == null || targetNode == null)
                throw new ArgumentException("Source or target node not found");

            var connection = new NodeConnection;
            {
                ConnectionId = Guid.NewGuid().ToString(),
                SourceNodeId = sourceNodeId,
                TargetNodeId = targetNodeId,
                OutputPort = outputPort,
                InputPort = inputPort,
                ConnectionType = connectionType;
            };

            var command = new AddConnectionCommand(_viewModel, connection);
            ExecuteCommand(command);

            return connection;
        }

        /// <summary>
        /// Deletes the selected connection;
        /// </summary>
        public void DeleteSelectedConnection()
        {
            if (_viewModel.SelectedConnection == null) return;

            var command = new DeleteConnectionCommand(_viewModel, _viewModel.SelectedConnection);
            ExecuteCommand(command);
        }

        /// <summary>
        /// Moves a node to new position;
        /// </summary>
        public void MoveNode(string nodeId, double newX, double newY)
        {
            var node = _viewModel.Nodes.FirstOrDefault(n => n.NodeId == nodeId);
            if (node == null) return;

            var oldX = node.X;
            var oldY = node.Y;

            var finalX = _viewModel.IsSnapToGridEnabled ? SnapToGrid(newX) : newX;
            var finalY = _viewModel.IsSnapToGridEnabled ? SnapToGrid(newY) : newY;

            var command = new MoveNodeCommand(node, oldX, oldY, finalX, finalY);
            ExecuteCommand(command);
        }

        /// <summary>
        /// Validates the entire graph;
        /// </summary>
        public GraphValidationResult ValidateGraph()
        {
            var result = _validator.Validate(_viewModel.Nodes.ToList(), _viewModel.Connections.ToList());

            GraphValidated?.Invoke(this, new GraphValidationEventArgs;
            {
                ValidationResult = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Compiles the graph to executable animation logic;
        /// </summary>
        public GraphCompilationResult CompileGraph()
        {
            var validationResult = ValidateGraph();
            if (!validationResult.IsValid)
            {
                throw new InvalidOperationException(
                    $"Graph validation failed: {string.Join(", ", validationResult.Errors)}");
            }

            var compilationResult = _compiler.Compile(_viewModel.Nodes.ToList(), _viewModel.Connections.ToList());

            GraphCompiled?.Invoke(this, new GraphCompilationEventArgs;
            {
                CompilationResult = compilationResult,
                Timestamp = DateTime.UtcNow;
            });

            return compilationResult;
        }

        /// <summary>
        /// Undo last operation;
        /// </summary>
        public void Undo()
        {
            if (_undoStack.Count > 0)
            {
                var command = _undoStack.Pop();
                command.Undo();
                _redoStack.Push(command);

                OnGraphModified($"Undo: {command.Description}");
            }
        }

        /// <summary>
        /// Redo last undone operation;
        /// </summary>
        public void Redo()
        {
            if (_redoStack.Count > 0)
            {
                var command = _redoStack.Pop();
                command.Execute();
                _undoStack.Push(command);

                OnGraphModified($"Redo: {command.Description}");
            }
        }

        /// <summary>
        /// Clears the entire graph;
        /// </summary>
        public void ClearGraph()
        {
            var nodes = _viewModel.Nodes.ToList();
            var connections = _viewModel.Connections.ToList();

            var command = new ClearGraphCommand(_viewModel, nodes, connections);
            ExecuteCommand(command);
        }

        /// <summary>
        /// Groups selected nodes;
        /// </summary>
        public void GroupSelectedNodes(string groupName)
        {
            var selectedNodes = _viewModel.Nodes.Where(n => n == _viewModel.SelectedNode).ToList();
            if (selectedNodes.Count < 2) return;

            var command = new GroupNodesCommand(_viewModel, selectedNodes, groupName);
            ExecuteCommand(command);
        }

        /// <summary>
        /// Saves graph to file;
        /// </summary>
        public void SaveGraph(string filePath)
        {
            var graphData = new GraphData;
            {
                Nodes = _viewModel.Nodes.ToList(),
                Connections = _viewModel.Connections.ToList(),
                Metadata = new GraphMetadata;
                {
                    Created = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow,
                    Version = "1.0"
                }
            };

            GraphSerializer.Serialize(graphData, filePath);
        }

        /// <summary>
        /// Loads graph from file;
        /// </summary>
        public void LoadGraph(string filePath)
        {
            var graphData = GraphSerializer.Deserialize(filePath);

            ClearGraph();

            foreach (var node in graphData.Nodes)
            {
                _viewModel.Nodes.Add(node);
            }

            foreach (var connection in graphData.Connections)
            {
                _viewModel.Connections.Add(connection);
            }

            OnGraphModified("Graph loaded from file");
        }

        private void ExecuteCommand(IGraphCommand command)
        {
            command.Execute();
            _undoStack.Push(command);
            _redoStack.Clear();

            OnGraphModified(command.Description);
        }

        private double SnapToGrid(double value, double gridSize = 10.0)
        {
            return Math.Round(value / gridSize) * gridSize;
        }

        private string GetDefaultNodeName(NodeType nodeType)
        {
            return nodeType switch;
            {
                NodeType.State => "NewState",
                NodeType.Transition => "Transition",
                NodeType.BlendSpace => "BlendSpace",
                NodeType.Animation => "Animation",
                NodeType.Condition => "Condition",
                NodeType.Variable => "Variable",
                _ => "Node"
            };
        }

        private void OnGraphModified(string description)
        {
            GraphModified?.Invoke(this, new GraphModifiedEventArgs;
            {
                Description = description,
                Timestamp = DateTime.UtcNow,
                NodeCount = _viewModel.Nodes.Count,
                ConnectionCount = _viewModel.Connections.Count;
            });
        }
    }

    #region Supporting Types and Interfaces;

    public enum NodeType;
    {
        State,
        Transition,
        BlendSpace,
        Animation,
        Condition,
        Variable,
        Event,
        Function;
    }

    public enum ConnectionType;
    {
        Execution,
        Data,
        Event,
        Reference;
    }

    public interface IAnimationGraphCompiler;
    {
        GraphCompilationResult Compile(List<GraphNode> nodes, List<NodeConnection> connections);
    }

    public interface IGraphValidator;
    {
        GraphValidationResult Validate(List<GraphNode> nodes, List<NodeConnection> connections);
    }

    public class GraphValidationResult;
    {
        public bool IsValid { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public class GraphCompilationResult;
    {
        public bool Success { get; set; }
        public byte[] CompiledBytecode { get; set; }
        public List<string> Messages { get; set; } = new List<string>();
        public TimeSpan CompilationTime { get; set; }
    }

    public class GraphData;
    {
        public List<GraphNode> Nodes { get; set; } = new List<GraphNode>();
        public List<NodeConnection> Connections { get; set; } = new List<NodeConnection>();
        public GraphMetadata Metadata { get; set; }
    }

    public class GraphMetadata;
    {
        public DateTime Created { get; set; }
        public DateTime LastModified { get; set; }
        public string Version { get; set; }
        public string Author { get; set; }
    }

    #endregion;

    #region Event Args;

    public class GraphModifiedEventArgs : EventArgs;
    {
        public string Description { get; set; }
        public DateTime Timestamp { get; set; }
        public int NodeCount { get; set; }
        public int ConnectionCount { get; set; }
    }

    public class GraphValidationEventArgs : EventArgs;
    {
        public GraphValidationResult ValidationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class GraphCompilationEventArgs : EventArgs;
    {
        public GraphCompilationResult CompilationResult { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Command Pattern Implementation;

    public interface IGraphCommand;
    {
        string Description { get; }
        void Execute();
        void Undo();
    }

    public class AddNodeCommand : IGraphCommand;
    {
        private readonly GraphViewModel _viewModel;
        private readonly GraphNode _node;

        public string Description => $"Add Node: {_node.NodeName}";

        public AddNodeCommand(GraphViewModel viewModel, GraphNode node)
        {
            _viewModel = viewModel;
            _node = node;
        }

        public void Execute()
        {
            _viewModel.Nodes.Add(_node);
        }

        public void Undo()
        {
            _viewModel.Nodes.Remove(_node);
            if (_viewModel.SelectedNode == _node)
            {
                _viewModel.SelectedNode = null;
            }
        }
    }

    public class DeleteNodeCommand : IGraphCommand;
    {
        private readonly GraphViewModel _viewModel;
        private readonly GraphNode _node;
        private readonly List<NodeConnection> _connections;

        public string Description => $"Delete Node: {_node.NodeName}";

        public DeleteNodeCommand(GraphViewModel viewModel, GraphNode node, List<NodeConnection> connections)
        {
            _viewModel = viewModel;
            _node = node;
            _connections = connections;
        }

        public void Execute()
        {
            _viewModel.Nodes.Remove(_node);
            foreach (var connection in _connections)
            {
                _viewModel.Connections.Remove(connection);
            }
            if (_viewModel.SelectedNode == _node)
            {
                _viewModel.SelectedNode = null;
            }
        }

        public void Undo()
        {
            _viewModel.Nodes.Add(_node);
            foreach (var connection in _connections)
            {
                _viewModel.Connections.Add(connection);
            }
        }
    }

    public class AddConnectionCommand : IGraphCommand;
    {
        private readonly GraphViewModel _viewModel;
        private readonly NodeConnection _connection;

        public string Description => "Add Connection";

        public AddConnectionCommand(GraphViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _connection = connection;
        }

        public void Execute()
        {
            _viewModel.Connections.Add(_connection);
        }

        public void Undo()
        {
            _viewModel.Connections.Remove(_connection);
            if (_viewModel.SelectedConnection == _connection)
            {
                _viewModel.SelectedConnection = null;
            }
        }
    }

    public class DeleteConnectionCommand : IGraphCommand;
    {
        private readonly GraphViewModel _viewModel;
        private readonly NodeConnection _connection;

        public string Description => "Delete Connection";

        public DeleteConnectionCommand(GraphViewModel viewModel, NodeConnection connection)
        {
            _viewModel = viewModel;
            _connection = connection;
        }

        public void Execute()
        {
            _viewModel.Connections.Remove(_connection);
            if (_viewModel.SelectedConnection == _connection)
            {
                _viewModel.SelectedConnection = null;
            }
        }

        public void Undo()
        {
            _viewModel.Connections.Add(_connection);
        }
    }

    public class MoveNodeCommand : IGraphCommand;
    {
        private readonly GraphNode _node;
        private readonly double _oldX;
        private readonly double _oldY;
        private readonly double _newX;
        private readonly double _newY;

        public string Description => $"Move Node: {_node.NodeName}";

        public MoveNodeCommand(GraphNode node, double oldX, double oldY, double newX, double newY)
        {
            _node = node;
            _oldX = oldX;
            _oldY = oldY;
            _newX = newX;
            _newY = newY;
        }

        public void Execute()
        {
            _node.X = _newX;
            _node.Y = _newY;
        }

        public void Undo()
        {
            _node.X = _oldX;
            _node.Y = _oldY;
        }
    }

    public class ClearGraphCommand : IGraphCommand;
    {
        private readonly GraphViewModel _viewModel;
        private readonly List<GraphNode> _originalNodes;
        private readonly List<NodeConnection> _originalConnections;

        public string Description => "Clear Graph";

        public ClearGraphCommand(GraphViewModel viewModel, List<GraphNode> nodes, List<NodeConnection> connections)
        {
            _viewModel = viewModel;
            _originalNodes = new List<GraphNode>(nodes);
            _originalConnections = new List<NodeConnection>(connections);
        }

        public void Execute()
        {
            _viewModel.Nodes.Clear();
            _viewModel.Connections.Clear();
            _viewModel.SelectedNode = null;
            _viewModel.SelectedConnection = null;
        }

        public void Undo()
        {
            foreach (var node in _originalNodes)
            {
                _viewModel.Nodes.Add(node);
            }
            foreach (var connection in _originalConnections)
            {
                _viewModel.Connections.Add(connection);
            }
        }
    }

    public class GroupNodesCommand : IGraphCommand;
    {
        private readonly GraphViewModel _viewModel;
        private readonly List<GraphNode> _nodes;
        private readonly string _groupName;
        private GraphNode _groupNode;

        public string Description => $"Group {_nodes.Count} Nodes";

        public GroupNodesCommand(GraphViewModel viewModel, List<GraphNode> nodes, string groupName)
        {
            _viewModel = viewModel;
            _nodes = nodes;
            _groupName = groupName;
        }

        public void Execute()
        {
            // Implementation for grouping nodes;
            // This would create a parent node and reorganize connections;
        }

        public void Undo()
        {
            // Implementation for ungrouping;
        }
    }

    #endregion;

    #region Serialization;

    public static class GraphSerializer;
    {
        public static void Serialize(GraphData graphData, string filePath)
        {
            // Implementation for serializing graph to file;
            // Using JSON, XML, or binary format;
        }

        public static GraphData Deserialize(string filePath)
        {
            // Implementation for deserializing graph from file;
            return new GraphData();
        }
    }

    #endregion;
}
