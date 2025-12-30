// NEDA.Brain/NLP_Engine/SyntaxAnalysis/SyntaxTree.cs;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Globalization;
using System.Collections;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis.Exceptions;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis.Contracts;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis.Configuration;
using NEDA.Brain.NLP_Engine.SyntaxAnalysis.Types;

namespace NEDA.Brain.NLP_Engine.SyntaxAnalysis;
{
    /// <summary>
    /// Advanced syntax tree representation for natural language processing.
    /// Supports multiple languages, dependency parsing, and semantic annotations.
    /// </summary>
    public class SyntaxTree : ISyntaxTree, ICloneable, IDisposable;
    {
        #region Private Fields;

        private readonly SyntaxTreeConfiguration _configuration;
        private readonly CultureInfo _cultureInfo;
        private readonly SyntaxTreeNode _root;
        private readonly List<SyntaxTreeNode> _nodes;
        private readonly Dictionary<int, SyntaxTreeNode> _nodeIndex;
        private readonly List<SyntaxDependency> _dependencies;
        private readonly Dictionary<string, object> _metadata;
        private readonly object _syncLock = new object();
        private bool _disposed;
        private int _nextNodeId;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the root node of the syntax tree.
        /// </summary>
        public ISyntaxTreeNode Root => _root;

        /// <summary>
        /// Gets all nodes in the tree.
        /// </summary>
        public IReadOnlyList<ISyntaxTreeNode> Nodes => _nodes.AsReadOnly();

        /// <summary>
        /// Gets all dependencies in the tree.
        /// </summary>
        public IReadOnlyList<SyntaxDependency> Dependencies => _dependencies.AsReadOnly();

        /// <summary>
        /// Gets the tree configuration.
        /// </summary>
        public SyntaxTreeConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the language code of the tree.
        /// </summary>
        public string LanguageCode => _configuration.LanguageCode;

        /// <summary>
        /// Gets the tree's metadata.
        /// </summary>
        public IReadOnlyDictionary<string, object> Metadata => _metadata;

        /// <summary>
        /// Gets the depth of the tree.
        /// </summary>
        public int Depth => CalculateDepth(_root);

        /// <summary>
        /// Gets the width of the tree (maximum children at any level).
        /// </summary>
        public int Width => CalculateWidth();

        /// <summary>
        /// Gets the total number of nodes in the tree.
        /// </summary>
        public int NodeCount => _nodes.Count;

        /// <summary>
        /// Gets the total number of dependencies in the tree.
        /// </summary>
        public int DependencyCount => _dependencies.Count;

        /// <summary>
        /// Gets whether the tree is empty.
        /// </summary>
        public bool IsEmpty => _root == null || _nodes.Count == 0;

        /// <summary>
        /// Gets whether the tree has been validated.
        /// </summary>
        public bool IsValidated { get; private set; }

        /// <summary>
        /// Gets whether the tree has semantic annotations.
        /// </summary>
        public bool HasSemanticAnnotations => _nodes.Any(n => n.SemanticRole != SemanticRole.None);

        /// <summary>
        /// Gets whether the tree has dependency annotations.
        /// </summary>
        public bool HasDependencies => _dependencies.Count > 0;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new empty syntax tree with default configuration.
        /// </summary>
        public SyntaxTree() : this(new SyntaxTreeConfiguration())
        {
        }

        /// <summary>
        /// Initializes a new empty syntax tree with specified configuration.
        /// </summary>
        /// <param name="configuration">The tree configuration.</param>
        public SyntaxTree(SyntaxTreeConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _cultureInfo = CultureInfo.GetCultureInfo(configuration.LanguageCode);

            _nodes = new List<SyntaxTreeNode>();
            _nodeIndex = new Dictionary<int, SyntaxTreeNode>();
            _dependencies = new List<SyntaxDependency>();
            _metadata = new Dictionary<string, object>();
            _nextNodeId = 1;

            InitializeMetadata();
        }

        /// <summary>
        /// Initializes a new syntax tree with a root node.
        /// </summary>
        /// <param name="rootToken">The root token text.</param>
        /// <param name="rootPos">The root part-of-speech tag.</param>
        /// <param name="configuration">The tree configuration.</param>
        public SyntaxTree(string rootToken, PartOfSpeech rootPos, SyntaxTreeConfiguration configuration)
            : this(configuration)
        {
            if (string.IsNullOrWhiteSpace(rootToken))
                throw new ArgumentException("Root token cannot be null or empty", nameof(rootToken));

            _root = CreateNode(rootToken, rootPos, null);
            AddNode(_root);
        }

        /// <summary>
        /// Initializes a new syntax tree from a parse string (bracketed format).
        /// </summary>
        /// <param name="parseString">The parse string in bracketed format.</param>
        /// <param name="configuration">The tree configuration.</param>
        public SyntaxTree(string parseString, SyntaxTreeConfiguration configuration)
            : this(configuration)
        {
            if (string.IsNullOrWhiteSpace(parseString))
                throw new ArgumentException("Parse string cannot be null or empty", nameof(parseString));

            BuildFromParseString(parseString);
            ValidateTree();
        }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        /// <param name="other">The tree to copy.</param>
        protected SyntaxTree(SyntaxTree other)
        {
            if (other == null)
                throw new ArgumentNullException(nameof(other));

            _configuration = other._configuration.Clone();
            _cultureInfo = CultureInfo.GetCultureInfo(_configuration.LanguageCode);
            _metadata = new Dictionary<string, object>(other._metadata);
            _nextNodeId = other._nextNodeId;

            // Deep copy nodes;
            _nodes = new List<SyntaxTreeNode>();
            _nodeIndex = new Dictionary<int, SyntaxTreeNode>();

            var nodeMap = new Dictionary<SyntaxTreeNode, SyntaxTreeNode>();

            foreach (var node in other._nodes)
            {
                var newNode = node.Clone();
                _nodes.Add(newNode);
                _nodeIndex[newNode.Id] = newNode;
                nodeMap[node] = newNode;
            }

            // Reconstruct parent-child relationships;
            foreach (var node in _nodes)
            {
                if (node.ParentId.HasValue && _nodeIndex.TryGetValue(node.ParentId.Value, out var parent))
                {
                    node.Parent = parent;
                    parent.AddChild(node);
                }
            }

            // Set root;
            if (other._root != null && nodeMap.TryGetValue(other._root, out var newRoot))
            {
                _root = newRoot;
            }

            // Deep copy dependencies;
            _dependencies = new List<SyntaxDependency>();
            foreach (var dep in other._dependencies)
            {
                var governor = dep.Governor != null && nodeMap.TryGetValue(dep.Governor, out var govNode) ? govNode : null;
                var dependent = dep.Dependent != null && nodeMap.TryGetValue(dep.Dependent, out var depNode) ? depNode : null;

                if (governor != null && dependent != null)
                {
                    _dependencies.Add(new SyntaxDependency(
                        dep.Relation,
                        governor,
                        dependent,
                        dep.Strength,
                        dep.Properties));
                }
            }

            IsValidated = other.IsValidated;
        }

        #endregion;

        #region Public Methods - Tree Construction;

        /// <summary>
        /// Creates and adds a new node to the tree.
        /// </summary>
        /// <param name="token">The token text.</param>
        /// <param name="partOfSpeech">The part-of-speech tag.</param>
        /// <param name="parent">The parent node (optional).</param>
        /// <returns>The created node.</returns>
        public ISyntaxTreeNode CreateNode(string token, PartOfSpeech partOfSpeech, ISyntaxTreeNode parent = null)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                var node = new SyntaxTreeNode(
                    id: _nextNodeId++,
                    token: token,
                    partOfSpeech: partOfSpeech,
                    parent: parent as SyntaxTreeNode,
                    configuration: _configuration);

                AddNode(node);

                if (parent != null && parent is SyntaxTreeNode parentNode)
                {
                    parentNode.AddChild(node);
                }
                else if (_root == null)
                {
                    _root = node;
                }

                return node;
            }
        }

        /// <summary>
        /// Adds an existing node to the tree.
        /// </summary>
        /// <param name="node">The node to add.</param>
        public void AddNode(ISyntaxTreeNode node)
        {
            ValidateNotDisposed();

            if (node == null)
                throw new ArgumentNullException(nameof(node));

            lock (_syncLock)
            {
                var syntaxNode = node as SyntaxTreeNode;
                if (syntaxNode == null)
                    throw new ArgumentException("Node must be of type SyntaxTreeNode", nameof(node));

                if (_nodeIndex.ContainsKey(syntaxNode.Id))
                    throw new InvalidOperationException($"Node with ID {syntaxNode.Id} already exists in the tree");

                _nodes.Add(syntaxNode);
                _nodeIndex[syntaxNode.Id] = syntaxNode;

                // Update next ID if necessary;
                if (syntaxNode.Id >= _nextNodeId)
                {
                    _nextNodeId = syntaxNode.Id + 1;
                }
            }
        }

        /// <summary>
        /// Removes a node from the tree.
        /// </summary>
        /// <param name="node">The node to remove.</param>
        /// <returns>True if the node was removed; otherwise, false.</returns>
        public bool RemoveNode(ISyntaxTreeNode node)
        {
            ValidateNotDisposed();

            if (node == null)
                return false;

            lock (_syncLock)
            {
                var syntaxNode = node as SyntaxTreeNode;
                if (syntaxNode == null || !_nodeIndex.ContainsKey(syntaxNode.Id))
                    return false;

                // Cannot remove root node;
                if (syntaxNode == _root)
                    throw new InvalidOperationException("Cannot remove the root node");

                // Remove all dependencies involving this node;
                _dependencies.RemoveAll(d => d.Governor == syntaxNode || d.Dependent == syntaxNode);

                // Remove from parent's children;
                if (syntaxNode.Parent != null)
                {
                    syntaxNode.Parent.RemoveChild(syntaxNode);
                }

                // Remove all descendants;
                RemoveDescendants(syntaxNode);

                // Remove the node itself;
                _nodes.Remove(syntaxNode);
                _nodeIndex.Remove(syntaxNode.Id);

                return true;
            }
        }

        /// <summary>
        /// Adds a dependency relationship between two nodes.
        /// </summary>
        /// <param name="relation">The dependency relation.</param>
        /// <param name="governor">The governor node.</param>
        /// <param name="dependent">The dependent node.</param>
        /// <param name="strength">The strength of the dependency (optional).</param>
        /// <param name="properties">Additional properties (optional).</param>
        public void AddDependency(
            string relation,
            ISyntaxTreeNode governor,
            ISyntaxTreeNode dependent,
            double strength = 1.0,
            Dictionary<string, object> properties = null)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(relation))
                throw new ArgumentException("Relation cannot be null or empty", nameof(relation));

            if (governor == null)
                throw new ArgumentNullException(nameof(governor));

            if (dependent == null)
                throw new ArgumentNullException(nameof(dependent));

            lock (_syncLock)
            {
                var govNode = governor as SyntaxTreeNode;
                var depNode = dependent as SyntaxTreeNode;

                if (govNode == null || depNode == null)
                    throw new ArgumentException("Nodes must be of type SyntaxTreeNode");

                if (!_nodeIndex.ContainsKey(govNode.Id) || !_nodeIndex.ContainsKey(depNode.Id))
                    throw new InvalidOperationException("Both nodes must belong to this tree");

                // Check for duplicate dependency;
                if (_dependencies.Any(d => d.Governor == govNode &&
                                          d.Dependent == depNode &&
                                          d.Relation == relation))
                {
                    throw new InvalidOperationException(
                        $"Dependency '{relation}' already exists between nodes {govNode.Id} and {depNode.Id}");
                }

                var dependency = new SyntaxDependency(
                    relation,
                    govNode,
                    depNode,
                    strength,
                    properties);

                _dependencies.Add(dependency);

                // Update node references;
                govNode.AddDependency(dependency, isGovernor: true);
                depNode.AddDependency(dependency, isGovernor: false);
            }
        }

        /// <summary>
        /// Removes a dependency from the tree.
        /// </summary>
        /// <param name="dependency">The dependency to remove.</param>
        /// <returns>True if the dependency was removed; otherwise, false.</returns>
        public bool RemoveDependency(SyntaxDependency dependency)
        {
            ValidateNotDisposed();

            if (dependency == null)
                return false;

            lock (_syncLock)
            {
                if (!_dependencies.Contains(dependency))
                    return false;

                // Remove from nodes;
                dependency.Governor?.RemoveDependency(dependency);
                dependency.Dependent?.RemoveDependency(dependency);

                // Remove from tree;
                return _dependencies.Remove(dependency);
            }
        }

        /// <summary>
        /// Builds a subtree from a specific node.
        /// </summary>
        /// <param name="rootNode">The root node of the subtree.</param>
        /// <returns>A new syntax tree representing the subtree.</returns>
        public SyntaxTree BuildSubtree(ISyntaxTreeNode rootNode)
        {
            ValidateNotDisposed();

            if (rootNode == null)
                throw new ArgumentNullException(nameof(rootNode));

            var node = rootNode as SyntaxTreeNode;
            if (node == null || !_nodeIndex.ContainsKey(node.Id))
                throw new ArgumentException("Node must belong to this tree", nameof(rootNode));

            var subtree = new SyntaxTree(_configuration);

            // Clone the subtree structure;
            CloneSubtree(node, null, subtree, new Dictionary<SyntaxTreeNode, SyntaxTreeNode>());

            // Clone relevant dependencies;
            CloneSubtreeDependencies(node, subtree);

            subtree.ValidateTree();
            return subtree;
        }

        /// <summary>
        /// Merges another tree into this tree.
        /// </summary>
        /// <param name="other">The tree to merge.</param>
        /// <param name="attachTo">The node to attach the other tree to (optional).</param>
        /// <param name="relation">The relation for attachment (optional).</param>
        public void MergeTree(SyntaxTree other, ISyntaxTreeNode attachTo = null, string relation = "conj")
        {
            ValidateNotDisposed();

            if (other == null)
                throw new ArgumentNullException(nameof(other));

            if (other == this)
                throw new InvalidOperationException("Cannot merge a tree with itself");

            lock (_syncLock)
            {
                other.ValidateNotDisposed();

                // Map old node IDs to new node IDs;
                var nodeMap = new Dictionary<SyntaxTreeNode, SyntaxTreeNode>();

                // Clone all nodes from other tree;
                foreach (var otherNode in other._nodes)
                {
                    var newNode = otherNode.Clone();
                    newNode.Id = _nextNodeId++;

                    _nodes.Add(newNode);
                    _nodeIndex[newNode.Id] = newNode;
                    nodeMap[otherNode] = newNode;
                }

                // Reconstruct parent-child relationships;
                foreach (var newNode in nodeMap.Values)
                {
                    if (newNode.ParentId.HasValue)
                    {
                        var oldParent = other._nodeIndex[newNode.ParentId.Value];
                        if (nodeMap.TryGetValue(oldParent, out var newParent))
                        {
                            newNode.Parent = newParent;
                            newParent.AddChild(newNode);
                        }
                    }
                }

                // Attach the other tree's root if specified;
                if (attachTo != null && other._root != null)
                {
                    var attachNode = attachTo as SyntaxTreeNode;
                    var otherRoot = nodeMap[other._root];

                    if (attachNode != null && _nodeIndex.ContainsKey(attachNode.Id))
                    {
                        attachNode.AddChild(otherRoot);
                        otherRoot.Parent = attachNode;

                        // Add a dependency relation;
                        AddDependency(relation, attachNode, otherRoot);
                    }
                }

                // Clone dependencies;
                foreach (var dep in other._dependencies)
                {
                    if (nodeMap.TryGetValue(dep.Governor, out var newGov) &&
                        nodeMap.TryGetValue(dep.Dependent, out var newDep))
                    {
                        AddDependency(
                            dep.Relation,
                            newGov,
                            newDep,
                            dep.Strength,
                            dep.Properties != null ? new Dictionary<string, object>(dep.Properties) : null);
                    }
                }

                // Merge metadata;
                foreach (var kvp in other._metadata)
                {
                    if (!_metadata.ContainsKey(kvp.Key))
                    {
                        _metadata[kvp.Key] = kvp.Value;
                    }
                }

                IsValidated = false;
            }
        }

        #endregion;

        #region Public Methods - Tree Navigation;

        /// <summary>
        /// Finds a node by its ID.
        /// </summary>
        /// <param name="nodeId">The node ID.</param>
        /// <returns>The node, or null if not found.</returns>
        public ISyntaxTreeNode FindNode(int nodeId)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _nodeIndex.TryGetValue(nodeId, out var node) ? node : null;
            }
        }

        /// <summary>
        /// Finds nodes by token text.
        /// </summary>
        /// <param name="token">The token text to search for.</param>
        /// <param name="ignoreCase">Whether to ignore case.</param>
        /// <returns>A list of matching nodes.</returns>
        public IList<ISyntaxTreeNode> FindNodesByToken(string token, bool ignoreCase = true)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(token))
                return new List<ISyntaxTreeNode>();

            var comparison = ignoreCase;
                ? StringComparison.OrdinalIgnoreCase;
                : StringComparison.Ordinal;

            lock (_syncLock)
            {
                return _nodes;
                    .Where(n => string.Equals(n.Token, token, comparison))
                    .Cast<ISyntaxTreeNode>()
                    .ToList();
            }
        }

        /// <summary>
        /// Finds nodes by part-of-speech tag.
        /// </summary>
        /// <param name="partOfSpeech">The part-of-speech tag to search for.</param>
        /// <returns>A list of matching nodes.</returns>
        public IList<ISyntaxTreeNode> FindNodesByPos(PartOfSpeech partOfSpeech)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _nodes;
                    .Where(n => n.PartOfSpeech == partOfSpeech)
                    .Cast<ISyntaxTreeNode>()
                    .ToList();
            }
        }

        /// <summary>
        /// Gets the leaves of the tree (terminal nodes).
        /// </summary>
        /// <returns>A list of leaf nodes.</returns>
        public IList<ISyntaxTreeNode> GetLeaves()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _nodes;
                    .Where(n => n.IsLeaf)
                    .Cast<ISyntaxTreeNode>()
                    .ToList();
            }
        }

        /// <summary>
        /// Gets the pre-terminal nodes (nodes whose children are all leaves).
        /// </summary>
        /// <returns>A list of pre-terminal nodes.</returns>
        public IList<ISyntaxTreeNode> GetPreTerminals()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                return _nodes;
                    .Where(n => n.Children.Count > 0 && n.Children.All(c => c.IsLeaf))
                    .Cast<ISyntaxTreeNode>()
                    .ToList();
            }
        }

        /// <summary>
        /// Gets all nodes at a specific depth.
        /// </summary>
        /// <param name="depth">The depth to search at.</param>
        /// <returns>A list of nodes at the specified depth.</returns>
        public IList<ISyntaxTreeNode> GetNodesAtDepth(int depth)
        {
            ValidateNotDisposed();

            if (depth < 0)
                return new List<ISyntaxTreeNode>();

            var result = new List<ISyntaxTreeNode>();
            CollectNodesAtDepth(_root, 0, depth, result);
            return result;
        }

        /// <summary>
        /// Gets the path from the root to a specific node.
        /// </summary>
        /// <param name="node">The target node.</param>
        /// <returns>The path as a list of nodes, or null if node not found.</returns>
        public IList<ISyntaxTreeNode> GetPathToNode(ISyntaxTreeNode node)
        {
            ValidateNotDisposed();

            if (node == null)
                return null;

            var syntaxNode = node as SyntaxTreeNode;
            if (syntaxNode == null || !_nodeIndex.ContainsKey(syntaxNode.Id))
                return null;

            var path = new List<ISyntaxTreeNode>();
            var current = syntaxNode;

            while (current != null)
            {
                path.Insert(0, current);
                current = current.Parent;
            }

            return path;
        }

        /// <summary>
        /// Gets the lowest common ancestor of two nodes.
        /// </summary>
        /// <param name="node1">The first node.</param>
        /// <param name="node2">The second node.</param>
        /// <returns>The lowest common ancestor, or null if not found.</returns>
        public ISyntaxTreeNode GetLowestCommonAncestor(ISyntaxTreeNode node1, ISyntaxTreeNode node2)
        {
            ValidateNotDisposed();

            if (node1 == null || node2 == null)
                return null;

            var n1 = node1 as SyntaxTreeNode;
            var n2 = node2 as SyntaxTreeNode;

            if (n1 == null || n2 == null ||
                !_nodeIndex.ContainsKey(n1.Id) ||
                !_nodeIndex.ContainsKey(n2.Id))
                return null;

            // Get paths to root;
            var path1 = GetPathToNode(n1);
            var path2 = GetPathToNode(n2);

            if (path1 == null || path2 == null)
                return null;

            // Find LCA;
            ISyntaxTreeNode lca = null;
            int minLength = Math.Min(path1.Count, path2.Count);

            for (int i = 0; i < minLength; i++)
            {
                if (path1[i] == path2[i])
                {
                    lca = path1[i];
                }
                else;
                {
                    break;
                }
            }

            return lca;
        }

        /// <summary>
        /// Gets the subtree rooted at a specific node.
        /// </summary>
        /// <param name="node">The root of the subtree.</param>
        /// <returns>All nodes in the subtree.</returns>
        public IList<ISyntaxTreeNode> GetSubtree(ISyntaxTreeNode node)
        {
            ValidateNotDisposed();

            if (node == null)
                return new List<ISyntaxTreeNode>();

            var syntaxNode = node as SyntaxTreeNode;
            if (syntaxNode == null || !_nodeIndex.ContainsKey(syntaxNode.Id))
                return new List<ISyntaxTreeNode>();

            var subtree = new List<ISyntaxTreeNode>();
            CollectSubtree(syntaxNode, subtree);
            return subtree;
        }

        /// <summary>
        /// Performs a breadth-first traversal of the tree.
        /// </summary>
        /// <returns>Nodes in breadth-first order.</returns>
        public IList<ISyntaxTreeNode> BreadthFirstTraversal()
        {
            ValidateNotDisposed();

            var result = new List<ISyntaxTreeNode>();

            if (_root == null)
                return result;

            var queue = new Queue<SyntaxTreeNode>();
            queue.Enqueue(_root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                foreach (var child in current.Children)
                {
                    queue.Enqueue(child as SyntaxTreeNode);
                }
            }

            return result;
        }

        /// <summary>
        /// Performs a depth-first traversal of the tree.
        /// </summary>
        /// <param name="order">The traversal order.</param>
        /// <returns>Nodes in depth-first order.</returns>
        public IList<ISyntaxTreeNode> DepthFirstTraversal(TraversalOrder order = TraversalOrder.PreOrder)
        {
            ValidateNotDisposed();

            var result = new List<ISyntaxTreeNode>();

            if (_root == null)
                return result;

            switch (order)
            {
                case TraversalOrder.PreOrder:
                    DepthFirstPreOrder(_root, result);
                    break;
                case TraversalOrder.InOrder:
                    DepthFirstInOrder(_root, result);
                    break;
                case TraversalOrder.PostOrder:
                    DepthFirstPostOrder(_root, result);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(order), order, null);
            }

            return result;
        }

        #endregion;

        #region Public Methods - Tree Analysis;

        /// <summary>
        /// Validates the tree structure.
        /// </summary>
        /// <returns>True if the tree is valid; otherwise, false.</returns>
        public bool ValidateTree()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                try
                {
                    // Check for null root;
                    if (_root == null)
                    {
                        throw new SyntaxTreeValidationException("Tree has no root node");
                    }

                    // Check for cycles;
                    if (HasCycles())
                    {
                        throw new SyntaxTreeValidationException("Tree contains cycles");
                    }

                    // Check for orphaned nodes;
                    var orphanedNodes = _nodes.Where(n => n != _root && n.Parent == null).ToList();
                    if (orphanedNodes.Count > 0)
                    {
                        throw new SyntaxTreeValidationException(
                            $"Tree contains {orphanedNodes.Count} orphaned nodes");
                    }

                    // Check node consistency;
                    foreach (var node in _nodes)
                    {
                        node.Validate();
                    }

                    // Check dependency consistency;
                    foreach (var dep in _dependencies)
                    {
                        if (dep.Governor == null || dep.Dependent == null)
                        {
                            throw new SyntaxTreeValidationException(
                                "Dependency has null governor or dependent");
                        }

                        if (!_nodeIndex.ContainsKey(dep.Governor.Id) ||
                            !_nodeIndex.ContainsKey(dep.Dependent.Id))
                        {
                            throw new SyntaxTreeValidationException(
                                "Dependency references nodes not in the tree");
                        }
                    }

                    IsValidated = true;
                    return true;
                }
                catch (SyntaxTreeValidationException)
                {
                    IsValidated = false;
                    throw;
                }
            }
        }

        /// <summary>
        /// Calculates various tree metrics.
        /// </summary>
        /// <returns>A dictionary of tree metrics.</returns>
        public Dictionary<string, double> CalculateMetrics()
        {
            ValidateNotDisposed();

            var metrics = new Dictionary<string, double>
            {
                ["NodeCount"] = NodeCount,
                ["Depth"] = Depth,
                ["Width"] = Width,
                ["AverageBranchingFactor"] = CalculateAverageBranchingFactor(),
                ["AverageDepth"] = CalculateAverageDepth(),
                ["DependencyCount"] = DependencyCount,
                ["LeafCount"] = GetLeaves().Count,
                ["PreTerminalCount"] = GetPreTerminals().Count,
                ["TreeBalance"] = CalculateBalance(),
                ["SemanticDensity"] = CalculateSemanticDensity()
            };

            // Add POS distribution;
            var posDistribution = CalculatePosDistribution();
            foreach (var kvp in posDistribution)
            {
                metrics[$"POS_{kvp.Key}"] = kvp.Value;
            }

            return metrics;
        }

        /// <summary>
        /// Calculates the part-of-speech distribution in the tree.
        /// </summary>
        /// <returns>A dictionary of POS tags to their frequencies.</returns>
        public Dictionary<string, double> CalculatePosDistribution()
        {
            ValidateNotDisposed();

            var distribution = new Dictionary<string, double>();
            int total = _nodes.Count;

            if (total == 0)
                return distribution;

            foreach (var node in _nodes)
            {
                var posName = node.PartOfSpeech.ToString();
                if (!distribution.ContainsKey(posName))
                {
                    distribution[posName] = 0;
                }
                distribution[posName]++;
            }

            // Convert to percentages;
            foreach (var key in distribution.Keys.ToList())
            {
                distribution[key] = (distribution[key] / total) * 100;
            }

            return distribution;
        }

        /// <summary>
        /// Extracts phrases of a specific type from the tree.
        /// </summary>
        /// <param name="phraseType">The type of phrase to extract.</param>
        /// <returns>A list of phrase subtrees.</returns>
        public IList<SyntaxTree> ExtractPhrases(PhraseType phraseType)
        {
            ValidateNotDisposed();

            var phrases = new List<SyntaxTree>();

            foreach (var node in _nodes)
            {
                if (node.PhraseType == phraseType && node.IsPhraseHead)
                {
                    phrases.Add(BuildSubtree(node));
                }
            }

            return phrases;
        }

        /// <summary>
        /// Finds the head word of the tree or subtree.
        /// </summary>
        /// <param name="subtreeRoot">The root of the subtree (optional).</param>
        /// <returns>The head word node.</returns>
        public ISyntaxTreeNode FindHeadWord(ISyntaxTreeNode subtreeRoot = null)
        {
            ValidateNotDisposed();

            var startNode = subtreeRoot as SyntaxTreeNode ?? _root;
            if (startNode == null)
                return null;

            return FindHeadWordRecursive(startNode);
        }

        /// <summary>
        /// Serializes the tree to a string representation.
        /// </summary>
        /// <param name="format">The serialization format.</param>
        /// <returns>The serialized tree.</returns>
        public string Serialize(TreeSerializationFormat format = TreeSerializationFormat.Bracketed)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                switch (format)
                {
                    case TreeSerializationFormat.Bracketed:
                        return SerializeToBracketedFormat();
                    case TreeSerializationFormat.Indented:
                        return SerializeToIndentedFormat();
                    case TreeSerializationFormat.Xml:
                        return SerializeToXmlFormat();
                    case TreeSerializationFormat.Json:
                        return SerializeToJsonFormat();
                    case TreeSerializationFormat.ConllU:
                        return SerializeToConllUFormat();
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format), format, null);
                }
            }
        }

        /// <summary>
        /// Deserializes a tree from a string representation.
        /// </summary>
        /// <param name="serialized">The serialized tree.</param>
        /// <param name="format">The serialization format.</param>
        public void Deserialize(string serialized, TreeSerializationFormat format = TreeSerializationFormat.Bracketed)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(serialized))
                throw new ArgumentException("Serialized string cannot be null or empty", nameof(serialized));

            lock (_syncLock)
            {
                Clear();

                switch (format)
                {
                    case TreeSerializationFormat.Bracketed:
                        BuildFromParseString(serialized);
                        break;
                    case TreeSerializationFormat.Xml:
                        BuildFromXml(serialized);
                        break;
                    case TreeSerializationFormat.Json:
                        BuildFromJson(serialized);
                        break;
                    case TreeSerializationFormat.ConllU:
                        BuildFromConllU(serialized);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(format), format, null);
                }

                ValidateTree();
            }
        }

        /// <summary>
        /// Adds metadata to the tree.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <param name="value">The metadata value.</param>
        public void AddMetadata(string key, object value)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Metadata key cannot be null or empty", nameof(key));

            lock (_syncLock)
            {
                _metadata[key] = value;
            }
        }

        /// <summary>
        /// Gets metadata from the tree.
        /// </summary>
        /// <param name="key">The metadata key.</param>
        /// <typeparam name="T">The type of the metadata value.</typeparam>
        /// <returns>The metadata value, or default if not found.</returns>
        public T GetMetadata<T>(string key)
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                if (_metadata.TryGetValue(key, out var value) && value is T typedValue)
                {
                    return typedValue;
                }
                return default;
            }
        }

        #endregion;

        #region Private Methods - Tree Construction;

        private void BuildFromParseString(string parseString)
        {
            // Remove extra whitespace;
            parseString = parseString.Trim();

            // Simple bracketed parser (can be enhanced for production)
            var stack = new Stack<SyntaxTreeNode>();
            var currentToken = new StringBuilder();
            var currentPos = new StringBuilder();
            var inToken = false;
            var inPos = false;

            SyntaxTreeNode currentNode = null;

            for (int i = 0; i < parseString.Length; i++)
            {
                char c = parseString[i];

                switch (c)
                {
                    case '(':
                        // Start of a new node;
                        inPos = true;
                        currentPos.Clear();
                        break;

                    case ' ':
                        if (inPos)
                        {
                            inPos = false;
                            inToken = true;
                            currentToken.Clear();
                        }
                        else if (inToken)
                        {
                            // End of token;
                            inToken = false;

                            var token = currentToken.ToString();
                            var posStr = currentPos.ToString();

                            if (!Enum.TryParse<PartOfSpeech>(posStr, true, out var pos))
                            {
                                pos = PartOfSpeech.UNKNOWN;
                            }

                            var node = CreateNode(token, pos, currentNode);

                            if (currentNode != null)
                            {
                                stack.Push(currentNode);
                            }

                            currentNode = node as SyntaxTreeNode;

                            if (_root == null)
                            {
                                _root = currentNode;
                            }
                        }
                        break;

                    case ')':
                        // End of current node;
                        if (inToken)
                        {
                            inToken = false;

                            var token = currentToken.ToString();
                            var posStr = currentPos.ToString();

                            if (!Enum.TryParse<PartOfSpeech>(posStr, true, out var pos))
                            {
                                pos = PartOfSpeech.UNKNOWN;
                            }

                            var node = CreateNode(token, pos, currentNode);

                            if (currentNode != null)
                            {
                                stack.Push(currentNode);
                            }

                            currentNode = node as SyntaxTreeNode;

                            if (_root == null)
                            {
                                _root = currentNode;
                            }
                        }

                        // Move back to parent;
                        if (stack.Count > 0)
                        {
                            currentNode = stack.Pop();
                        }
                        break;

                    default:
                        if (inPos)
                        {
                            currentPos.Append(c);
                        }
                        else if (inToken)
                        {
                            currentToken.Append(c);
                        }
                        break;
                }
            }
        }

        private void BuildFromXml(string xml)
        {
            // XML parsing implementation;
            // This is a placeholder - implement proper XML parsing;
            throw new NotImplementedException("XML parsing not implemented");
        }

        private void BuildFromJson(string json)
        {
            // JSON parsing implementation;
            // This is a placeholder - implement proper JSON parsing;
            throw new NotImplementedException("JSON parsing not implemented");
        }

        private void BuildFromConllU(string conllu)
        {
            // CoNLL-U format parsing implementation;
            // This is a placeholder - implement proper CoNLL-U parsing;
            throw new NotImplementedException("CoNLL-U parsing not implemented");
        }

        private void RemoveDescendants(SyntaxTreeNode node)
        {
            var descendants = new List<SyntaxTreeNode>();
            CollectSubtree(node, descendants);

            // Remove from bottom up;
            foreach (var descendant in descendants.OrderByDescending(n => n.Depth))
            {
                if (descendant != node && _nodeIndex.ContainsKey(descendant.Id))
                {
                    // Remove dependencies involving this descendant;
                    _dependencies.RemoveAll(d => d.Governor == descendant || d.Dependent == descendant);

                    // Remove from parent's children;
                    if (descendant.Parent != null)
                    {
                        descendant.Parent.RemoveChild(descendant);
                    }

                    // Remove the node;
                    _nodes.Remove(descendant);
                    _nodeIndex.Remove(descendant.Id);
                }
            }
        }

        private void CloneSubtree(
            SyntaxTreeNode sourceNode,
            SyntaxTreeNode parent,
            SyntaxTree targetTree,
            Dictionary<SyntaxTreeNode, SyntaxTreeNode> nodeMap)
        {
            var clonedNode = sourceNode.Clone();
            clonedNode.Id = targetTree._nextNodeId++;
            clonedNode.Parent = parent;

            targetTree._nodes.Add(clonedNode);
            targetTree._nodeIndex[clonedNode.Id] = clonedNode;
            nodeMap[sourceNode] = clonedNode;

            if (parent != null)
            {
                parent.AddChild(clonedNode);
            }
            else;
            {
                targetTree._root = clonedNode;
            }

            foreach (var child in sourceNode.Children)
            {
                CloneSubtree(child as SyntaxTreeNode, clonedNode, targetTree, nodeMap);
            }
        }

        private void CloneSubtreeDependencies(SyntaxTreeNode rootNode, SyntaxTree targetTree)
        {
            // Clone dependencies where both governor and dependent are in the subtree;
            var nodeMap = targetTree._nodes.ToDictionary(n => (n as SyntaxTreeNode).OriginalId, n => n as SyntaxTreeNode);

            foreach (var dep in _dependencies)
            {
                if (nodeMap.TryGetValue(dep.Governor.OriginalId, out var govNode) &&
                    nodeMap.TryGetValue(dep.Dependent.OriginalId, out var depNode))
                {
                    targetTree.AddDependency(
                        dep.Relation,
                        govNode,
                        depNode,
                        dep.Strength,
                        dep.Properties != null ? new Dictionary<string, object>(dep.Properties) : null);
                }
            }
        }

        #endregion;

        #region Private Methods - Tree Navigation;

        private void CollectNodesAtDepth(SyntaxTreeNode node, int currentDepth, int targetDepth, List<ISyntaxTreeNode> result)
        {
            if (node == null)
                return;

            if (currentDepth == targetDepth)
            {
                result.Add(node);
                return;
            }

            foreach (var child in node.Children)
            {
                CollectNodesAtDepth(child as SyntaxTreeNode, currentDepth + 1, targetDepth, result);
            }
        }

        private void CollectSubtree(SyntaxTreeNode node, List<ISyntaxTreeNode> result)
        {
            if (node == null)
                return;

            result.Add(node);

            foreach (var child in node.Children)
            {
                CollectSubtree(child as SyntaxTreeNode, result);
            }
        }

        private void DepthFirstPreOrder(SyntaxTreeNode node, List<ISyntaxTreeNode> result)
        {
            if (node == null)
                return;

            result.Add(node);

            foreach (var child in node.Children)
            {
                DepthFirstPreOrder(child as SyntaxTreeNode, result);
            }
        }

        private void DepthFirstInOrder(SyntaxTreeNode node, List<ISyntaxTreeNode> result)
        {
            if (node == null)
                return;

            var children = node.Children;
            int half = children.Count / 2;

            // Process left half;
            for (int i = 0; i < half; i++)
            {
                DepthFirstInOrder(children[i] as SyntaxTreeNode, result);
            }

            result.Add(node);

            // Process right half;
            for (int i = half; i < children.Count; i++)
            {
                DepthFirstInOrder(children[i] as SyntaxTreeNode, result);
            }
        }

        private void DepthFirstPostOrder(SyntaxTreeNode node, List<ISyntaxTreeNode> result)
        {
            if (node == null)
                return;

            foreach (var child in node.Children)
            {
                DepthFirstPostOrder(child as SyntaxTreeNode, result);
            }

            result.Add(node);
        }

        private SyntaxTreeNode FindHeadWordRecursive(SyntaxTreeNode node)
        {
            if (node == null || node.IsLeaf)
                return node;

            // Use head rules based on phrase type and language;
            SyntaxTreeNode headChild = null;

            switch (node.PhraseType)
            {
                case PhraseType.NounPhrase:
                    headChild = FindNounPhraseHead(node);
                    break;
                case PhraseType.VerbPhrase:
                    headChild = FindVerbPhraseHead(node);
                    break;
                case PhraseType.PrepositionalPhrase:
                    headChild = FindPrepositionalPhraseHead(node);
                    break;
                case PhraseType.AdjectivePhrase:
                    headChild = FindAdjectivePhraseHead(node);
                    break;
                case PhraseType.AdverbPhrase:
                    headChild = FindAdverbPhraseHead(node);
                    break;
                default:
                    // Default to rightmost child for other phrase types;
                    headChild = node.Children.LastOrDefault() as SyntaxTreeNode;
                    break;
            }

            return headChild != null ? FindHeadWordRecursive(headChild) : node;
        }

        private SyntaxTreeNode FindNounPhraseHead(SyntaxTreeNode node)
        {
            // English NP head rules: look for nouns right to left;
            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i] as SyntaxTreeNode;
                if (child.PartOfSpeech == PartOfSpeech.NOUN ||
                    child.PartOfSpeech == PartOfSpeech.PROPN ||
                    child.PartOfSpeech == PartOfSpeech.PRON)
                {
                    return child;
                }

                // Check for nested NPs;
                if (child.PhraseType == PhraseType.NounPhrase)
                {
                    return child;
                }
            }

            return node.Children.LastOrDefault() as SyntaxTreeNode;
        }

        private SyntaxTreeNode FindVerbPhraseHead(SyntaxTreeNode node)
        {
            // English VP head rules: look for verbs left to right;
            foreach (var child in node.Children)
            {
                var syntaxChild = child as SyntaxTreeNode;
                if (syntaxChild.PartOfSpeech == PartOfSpeech.VERB ||
                    syntaxChild.PartOfSpeech == PartOfSpeech.AUX)
                {
                    return syntaxChild;
                }

                // Check for nested VPs;
                if (syntaxChild.PhraseType == PhraseType.VerbPhrase)
                {
                    return syntaxChild;
                }
            }

            return node.Children.FirstOrDefault() as SyntaxTreeNode;
        }

        private SyntaxTreeNode FindPrepositionalPhraseHead(SyntaxTreeNode node)
        {
            // PP head is the preposition;
            foreach (var child in node.Children)
            {
                var syntaxChild = child as SyntaxTreeNode;
                if (syntaxChild.PartOfSpeech == PartOfSpeech.ADP)
                {
                    return syntaxChild;
                }
            }

            return node.Children.FirstOrDefault() as SyntaxTreeNode;
        }

        private SyntaxTreeNode FindAdjectivePhraseHead(SyntaxTreeNode node)
        {
            // AP head is the adjective;
            foreach (var child in node.Children)
            {
                var syntaxChild = child as SyntaxTreeNode;
                if (syntaxChild.PartOfSpeech == PartOfSpeech.ADJ)
                {
                    return syntaxChild;
                }
            }

            return node.Children.FirstOrDefault() as SyntaxTreeNode;
        }

        private SyntaxTreeNode FindAdverbPhraseHead(SyntaxTreeNode node)
        {
            // AdvP head is the adverb;
            foreach (var child in node.Children)
            {
                var syntaxChild = child as SyntaxTreeNode;
                if (syntaxChild.PartOfSpeech == PartOfSpeech.ADV)
                {
                    return syntaxChild;
                }
            }

            return node.Children.FirstOrDefault() as SyntaxTreeNode;
        }

        #endregion;

        #region Private Methods - Tree Analysis;

        private bool HasCycles()
        {
            var visited = new HashSet<int>();
            var recursionStack = new HashSet<int>();

            foreach (var node in _nodes)
            {
                if (HasCyclesDFS(node, visited, recursionStack))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasCyclesDFS(SyntaxTreeNode node, HashSet<int> visited, HashSet<int> recursionStack)
        {
            if (!visited.Contains(node.Id))
            {
                visited.Add(node.Id);
                recursionStack.Add(node.Id);

                foreach (var child in node.Children)
                {
                    var childNode = child as SyntaxTreeNode;
                    if (!visited.Contains(childNode.Id) &&
                        HasCyclesDFS(childNode, visited, recursionStack))
                    {
                        return true;
                    }
                    else if (recursionStack.Contains(childNode.Id))
                    {
                        return true;
                    }
                }
            }

            recursionStack.Remove(node.Id);
            return false;
        }

        private int CalculateDepth(SyntaxTreeNode node)
        {
            if (node == null)
                return 0;

            if (node.IsLeaf)
                return 1;

            int maxChildDepth = 0;
            foreach (var child in node.Children)
            {
                int childDepth = CalculateDepth(child as SyntaxTreeNode);
                maxChildDepth = Math.Max(maxChildDepth, childDepth);
            }

            return maxChildDepth + 1;
        }

        private int CalculateWidth()
        {
            if (_root == null)
                return 0;

            int maxWidth = 0;
            var currentLevel = new Queue<SyntaxTreeNode>();
            var nextLevel = new Queue<SyntaxTreeNode>();

            currentLevel.Enqueue(_root);

            while (currentLevel.Count > 0)
            {
                int levelWidth = currentLevel.Count;
                maxWidth = Math.Max(maxWidth, levelWidth);

                while (currentLevel.Count > 0)
                {
                    var node = currentLevel.Dequeue();
                    foreach (var child in node.Children)
                    {
                        nextLevel.Enqueue(child as SyntaxTreeNode);
                    }
                }

                // Swap queues;
                var temp = currentLevel;
                currentLevel = nextLevel;
                nextLevel = temp;
                nextLevel.Clear();
            }

            return maxWidth;
        }

        private double CalculateAverageBranchingFactor()
        {
            if (_nodes.Count == 0)
                return 0;

            int totalChildren = 0;
            int nonLeafNodes = 0;

            foreach (var node in _nodes)
            {
                if (!node.IsLeaf)
                {
                    totalChildren += node.Children.Count;
                    nonLeafNodes++;
                }
            }

            return nonLeafNodes > 0 ? (double)totalChildren / nonLeafNodes : 0;
        }

        private double CalculateAverageDepth()
        {
            if (_nodes.Count == 0)
                return 0;

            int totalDepth = 0;
            foreach (var node in _nodes)
            {
                totalDepth += node.Depth;
            }

            return (double)totalDepth / _nodes.Count;
        }

        private double CalculateBalance()
        {
            if (_root == null)
                return 1.0;

            return CalculateNodeBalance(_root);
        }

        private double CalculateNodeBalance(SyntaxTreeNode node)
        {
            if (node.IsLeaf)
                return 1.0;

            var childBalances = node.Children;
                .Select(c => CalculateNodeBalance(c as SyntaxTreeNode))
                .ToList();

            if (childBalances.Count == 0)
                return 1.0;

            double avgBalance = childBalances.Average();
            double depthRatio = (double)node.MinChildDepth / node.MaxChildDepth;

            return (avgBalance + depthRatio) / 2;
        }

        private double CalculateSemanticDensity()
        {
            if (_nodes.Count == 0)
                return 0;

            int semanticNodes = _nodes.Count(n => n.SemanticRole != SemanticRole.None);
            return (double)semanticNodes / _nodes.Count;
        }

        #endregion;

        #region Private Methods - Serialization;

        private string SerializeToBracketedFormat()
        {
            var sb = new StringBuilder();
            SerializeNodeBracketed(_root, sb, 0);
            return sb.ToString();
        }

        private void SerializeNodeBracketed(SyntaxTreeNode node, StringBuilder sb, int indent)
        {
            if (node == null)
                return;

            sb.Append(' ', indent * 2);
            sb.Append('(');
            sb.Append(node.PartOfSpeech);
            sb.Append(' ');
            sb.Append(node.Token);

            if (node.Children.Count > 0)
            {
                sb.AppendLine();

                foreach (var child in node.Children)
                {
                    SerializeNodeBracketed(child as SyntaxTreeNode, sb, indent + 1);
                }

                sb.Append(' ', indent * 2);
            }

            sb.Append(')');

            if (indent > 0)
            {
                sb.AppendLine();
            }
        }

        private string SerializeToIndentedFormat()
        {
            var sb = new StringBuilder();
            SerializeNodeIndented(_root, sb, 0);
            return sb.ToString();
        }

        private void SerializeNodeIndented(SyntaxTreeNode node, StringBuilder sb, int depth)
        {
            if (node == null)
                return;

            sb.Append(' ', depth * 2);
            sb.Append(node.Token);
            sb.Append(" [");
            sb.Append(node.PartOfSpeech);

            if (node.PhraseType != PhraseType.None)
            {
                sb.Append(", ");
                sb.Append(node.PhraseType);
            }

            if (node.SemanticRole != SemanticRole.None)
            {
                sb.Append(", ");
                sb.Append(node.SemanticRole);
            }

            sb.Append(']');
            sb.AppendLine();

            foreach (var child in node.Children)
            {
                SerializeNodeIndented(child as SyntaxTreeNode, sb, depth + 1);
            }
        }

        private string SerializeToXmlFormat()
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<syntax-tree>");
            SerializeNodeXml(_root, sb, 1);
            sb.AppendLine("</syntax-tree>");
            return sb.ToString();
        }

        private void SerializeNodeXml(SyntaxTreeNode node, StringBuilder sb, int depth)
        {
            if (node == null)
                return;

            var indent = new string(' ', depth * 2);
            sb.Append(indent);
            sb.Append("<node");
            sb.Append($" id=\"{node.Id}\"");
            sb.Append($" token=\"{EscapeXml(node.Token)}\"");
            sb.Append($" pos=\"{node.PartOfSpeech}\"");

            if (node.PhraseType != PhraseType.None)
            {
                sb.Append($" phrase-type=\"{node.PhraseType}\"");
            }

            if (node.SemanticRole != SemanticRole.None)
            {
                sb.Append($" semantic-role=\"{node.SemanticRole}\"");
            }

            if (node.Children.Count == 0)
            {
                sb.Append(" />");
            }
            else;
            {
                sb.AppendLine(">");

                foreach (var child in node.Children)
                {
                    SerializeNodeXml(child as SyntaxTreeNode, sb, depth + 1);
                }

                sb.Append(indent);
                sb.Append("</node>");
            }

            sb.AppendLine();
        }

        private string SerializeToJsonFormat()
        {
            var json = new StringBuilder();
            json.AppendLine("{");
            SerializeNodeJson(_root, json, 1);
            json.AppendLine("}");
            return json.ToString();
        }

        private void SerializeNodeJson(SyntaxTreeNode node, StringBuilder json, int depth)
        {
            if (node == null)
                return;

            var indent = new string(' ', depth * 2);
            json.Append(indent);
            json.AppendLine("{");

            json.Append(indent);
            json.Append("  \"id\": ");
            json.Append(node.Id);
            json.AppendLine(",");

            json.Append(indent);
            json.Append("  \"token\": \"");
            json.Append(EscapeJson(node.Token));
            json.AppendLine("\",");

            json.Append(indent);
            json.Append("  \"pos\": \"");
            json.Append(node.PartOfSpeech);
            json.AppendLine("\",");

            if (node.PhraseType != PhraseType.None)
            {
                json.Append(indent);
                json.Append("  \"phraseType\": \"");
                json.Append(node.PhraseType);
                json.AppendLine("\",");
            }

            if (node.SemanticRole != SemanticRole.None)
            {
                json.Append(indent);
                json.Append("  \"semanticRole\": \"");
                json.Append(node.SemanticRole);
                json.AppendLine("\",");
            }

            json.Append(indent);
            json.Append("  \"children\": [");

            if (node.Children.Count > 0)
            {
                json.AppendLine();

                for (int i = 0; i < node.Children.Count; i++)
                {
                    SerializeNodeJson(node.Children[i] as SyntaxTreeNode, json, depth + 2);

                    if (i < node.Children.Count - 1)
                    {
                        json.AppendLine(",");
                    }
                }

                json.AppendLine();
                json.Append(indent);
                json.Append("  ]");
            }
            else;
            {
                json.Append("]");
            }

            json.AppendLine();
            json.Append(indent);
            json.Append("}");
        }

        private string SerializeToConllUFormat()
        {
            var sb = new StringBuilder();

            // CoNLL-U header;
            sb.AppendLine("# sent_id = 1");
            sb.AppendLine("# text = " + GetSentenceText());
            sb.AppendLine();

            // Get nodes in sentence order (leaves)
            var leaves = GetLeaves();

            for (int i = 0; i < leaves.Count; i++)
            {
                var node = leaves[i] as SyntaxTreeNode;

                // ID;
                sb.Append(i + 1);
                sb.Append('\t');

                // FORM;
                sb.Append(node.Token);
                sb.Append('\t');

                // LEMMA (placeholder)
                sb.Append(node.Token.ToLower(_cultureInfo));
                sb.Append('\t');

                // UPOS;
                sb.Append(node.PartOfSpeech);
                sb.Append('\t');

                // XPOS (placeholder)
                sb.Append(node.PartOfSpeech);
                sb.Append('\t');

                // FEATS (placeholder)
                sb.Append("_");
                sb.Append('\t');

                // HEAD (find governor)
                int headId = 0;
                var dep = _dependencies.FirstOrDefault(d => d.Dependent == node);
                if (dep != null)
                {
                    var governorIndex = leaves.IndexOf(dep.Governor);
                    headId = governorIndex + 1;
                }
                sb.Append(headId);
                sb.Append('\t');

                // DEPREL;
                sb.Append(dep?.Relation ?? "_");
                sb.Append('\t');

                // DEPS (placeholder)
                sb.Append("_");
                sb.Append('\t');

                // MISC (placeholder)
                sb.Append("_");

                sb.AppendLine();
            }

            sb.AppendLine();
            return sb.ToString();
        }

        private string GetSentenceText()
        {
            var leaves = GetLeaves();
            return string.Join(" ", leaves.Select(n => n.Token));
        }

        private string EscapeXml(string text)
        {
            return text;
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private string EscapeJson(string text)
        {
            return text;
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\b", "\\b")
                .Replace("\f", "\\f")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r")
                .Replace("\t", "\\t");
        }

        #endregion;

        #region Private Methods - Initialization;

        private void InitializeMetadata()
        {
            _metadata["CreationTime"] = DateTime.UtcNow;
            _metadata["Language"] = _configuration.LanguageCode;
            _metadata["Version"] = "1.0";
            _metadata["TreeType"] = "SyntaxTree";
        }

        private void Clear()
        {
            _nodes.Clear();
            _nodeIndex.Clear();
            _dependencies.Clear();
            _metadata.Clear();
            _root = null;
            _nextNodeId = 1;
            IsValidated = false;

            InitializeMetadata();
        }

        #endregion;

        #region Validation Methods;

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SyntaxTree));
        }

        #endregion;

        #region ICloneable Implementation;

        /// <summary>
        /// Creates a deep copy of the syntax tree.
        /// </summary>
        /// <returns>A deep copy of the syntax tree.</returns>
        public object Clone()
        {
            return new SyntaxTree(this);
        }

        /// <summary>
        /// Creates a deep copy of the syntax tree.
        /// </summary>
        /// <returns>A deep copy of the syntax tree.</returns>
        public SyntaxTree DeepClone()
        {
            return new SyntaxTree(this);
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose managed resources;
                    _nodes.Clear();
                    _nodeIndex.Clear();
                    _dependencies.Clear();
                    _metadata.Clear();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~SyntaxTree()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Tree traversal order.
        /// </summary>
        public enum TraversalOrder;
        {
            PreOrder,
            InOrder,
            PostOrder;
        }

        /// <summary>
        /// Tree serialization format.
        /// </summary>
        public enum TreeSerializationFormat;
        {
            Bracketed,
            Indented,
            Xml,
            Json,
            ConllU;
        }

        #endregion;
    }
}
