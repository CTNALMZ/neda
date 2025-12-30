// NEDA.KnowledgeBase/LocalDB/KnowledgeBase.cs;

using Microsoft.Data.Sqlite;
using NEDA.Brain.KnowledgeBase.CreativePatterns;
using NEDA.Brain.KnowledgeBase.FactDatabase;
using NEDA.Brain.MemorySystem.LongTermMemory;
using NEDA.Brain.NLP_Engine.SemanticUnderstanding;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.KnowledgeBase.LocalDB.Backup;
using NEDA.KnowledgeBase.LocalDB.Configuration;
using NEDA.KnowledgeBase.LocalDB.Contracts;
using NEDA.KnowledgeBase.LocalDB.Exceptions;
using NEDA.KnowledgeBase.LocalDB.Indexing;
using NEDA.KnowledgeBase.LocalDB.Migration;
using NEDA.KnowledgeBase.LocalDB.Models;
using NEDA.KnowledgeBase.LocalDB.Search;
using NEDA.KnowledgeBase.LocalDB.Security;
using NEDA.KnowledgeBase.LocalDB.Validators;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.SecurityModules.Encryption;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace NEDA.KnowledgeBase.LocalDB;
{
    /// <summary>
    /// Advanced knowledge base system with support for multiple data types,
    /// full-text search, semantic indexing, and comprehensive data management.
    /// </summary>
    public class KnowledgeBase : IKnowledgeBase, IDisposable;
    {
        #region Constants;

        private const int DEFAULT_MAX_CONNECTIONS = 10;
        private const int DEFAULT_QUERY_TIMEOUT_SECONDS = 30;
        private const int DEFAULT_BATCH_SIZE = 1000;
        private const int DEFAULT_CACHE_SIZE_MB = 100;
        private const string DEFAULT_DATABASE_FILE = "knowledgebase.db";
        private const string BACKUP_FILE_EXTENSION = ".kbbackup";
        private const string INDEX_FILE_EXTENSION = ".kbindex";
        private const string METADATA_FILE_NAME = "metadata.json";

        #endregion;

        #region Private Fields;

        private readonly KnowledgeBaseConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly IKnowledgeBaseValidator _validator;
        private readonly IKnowledgeBaseIndexer _indexer;
        private readonly IKnowledgeBaseSearcher _searcher;
        private readonly IKnowledgeBaseBackupManager _backupManager;
        private readonly IKnowledgeBaseMigrationManager _migrationManager;
        private readonly IKnowledgeBaseSecurityManager _securityManager;
        private readonly SqliteConnection _connection;
        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _connectionSemaphore;
        private readonly Dictionary<string, object> _caches;
        private readonly JsonSerializerOptions _jsonOptions;
        private bool _disposed;
        private bool _isInitialized;
        private bool _isOpen;
        private int _totalQueries;
        private int _totalInserts;
        private int _totalUpdates;
        private int _totalDeletes;
        private long _totalQueryTimeMs;
        private long _peakMemoryUsage;
        private DateTime _lastBackupTime;
        private DateTime _lastOptimizationTime;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the configuration used by this knowledge base.
        /// </summary>
        public KnowledgeBaseConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the database file path.
        /// </summary>
        public string DatabaseFilePath { get; private set; }

        /// <summary>
        /// Gets whether the knowledge base is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets whether the knowledge base is open.
        /// </summary>
        public bool IsOpen => _isOpen;

        /// <summary>
        /// Gets the total number of queries executed.
        /// </summary>
        public int TotalQueries => _totalQueries;

        /// <summary>
        /// Gets the total number of inserts performed.
        /// </summary>
        public int TotalInserts => _totalInserts;

        /// <summary>
        /// Gets the total number of updates performed.
        /// </summary>
        public int TotalUpdates => _totalUpdates;

        /// <summary>
        /// Gets the total number of deletes performed.
        /// </summary>
        public int TotalDeletes => _totalDeletes;

        /// <summary>
        /// Gets the average query time in milliseconds.
        /// </summary>
        public double AverageQueryTimeMs => _totalQueries > 0 ?
            (double)_totalQueryTimeMs / _totalQueries : 0;

        /// <summary>
        /// Gets the peak memory usage in bytes.
        /// </summary>
        public long PeakMemoryUsageBytes => _peakMemoryUsage;

        /// <summary>
        /// Gets the knowledge base statistics.
        /// </summary>
        public KnowledgeBaseStatistics Statistics => GetStatistics();

        /// <summary>
        /// Gets the database schema version.
        /// </summary>
        public Version SchemaVersion { get; private set; }

        /// <summary>
        /// Gets the knowledge base metadata.
        /// </summary>
        public KnowledgeBaseMetadata Metadata { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Occurs when the knowledge base is opened.
        /// </summary>
        public event EventHandler<KnowledgeBaseOpenedEventArgs> KnowledgeBaseOpened;

        /// <summary>
        /// Occurs when the knowledge base is closed.
        /// </summary>
        public event EventHandler<KnowledgeBaseClosedEventArgs> KnowledgeBaseClosed;

        /// <summary>
        /// Occurs when a knowledge item is added.
        /// </summary>
        public event EventHandler<KnowledgeItemAddedEventArgs> KnowledgeItemAdded;

        /// <summary>
        /// Occurs when a knowledge item is updated.
        /// </summary>
        public event EventHandler<KnowledgeItemUpdatedEventArgs> KnowledgeItemUpdated;

        /// <summary>
        /// Occurs when a knowledge item is deleted.
        /// </summary>
        public event EventHandler<KnowledgeItemDeletedEventArgs> KnowledgeItemDeleted;

        /// <summary>
        /// Occurs when the knowledge base is backed up.
        /// </summary>
        public event EventHandler<KnowledgeBaseBackupEventArgs> KnowledgeBaseBackedUp;

        /// <summary>
        /// Occurs when the knowledge base is optimized.
        /// </summary>
        public event EventHandler<KnowledgeBaseOptimizedEventArgs> KnowledgeBaseOptimized;

        /// <summary>
        /// Occurs when a query is executed.
        /// </summary>
        public event EventHandler<KnowledgeBaseQueryEventArgs> QueryExecuted;

        /// <summary>
        /// Occurs when an index is built.
        /// </summary>
        public event EventHandler<IndexBuiltEventArgs> IndexBuilt;

        /// <summary>
        /// Occurs when the schema is migrated.
        /// </summary>
        public event EventHandler<SchemaMigratedEventArgs> SchemaMigrated;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of the KnowledgeBase class with default configuration.
        /// </summary>
        public KnowledgeBase()
            : this(new KnowledgeBaseConfiguration(), null, null, null, null, null, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the KnowledgeBase class with specified configuration.
        /// </summary>
        /// <param name="configuration">The knowledge base configuration.</param>
        /// <param name="logger">The logger instance (optional).</param>
        /// <param name="validator">The knowledge base validator (optional).</param>
        /// <param name="indexer">The knowledge base indexer (optional).</param>
        /// <param name="searcher">The knowledge base searcher (optional).</param>
        /// <param name="backupManager">The backup manager (optional).</param>
        /// <param name="migrationManager">The migration manager (optional).</param>
        /// <param name="securityManager">The security manager (optional).</param>
        public KnowledgeBase(
            KnowledgeBaseConfiguration configuration,
            ILogger logger = null,
            IKnowledgeBaseValidator validator = null,
            IKnowledgeBaseIndexer indexer = null,
            IKnowledgeBaseSearcher searcher = null,
            IKnowledgeBaseBackupManager backupManager = null,
            IKnowledgeBaseMigrationManager migrationManager = null,
            IKnowledgeBaseSecurityManager securityManager = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? LogManager.GetLogger(typeof(KnowledgeBase));
            _validator = validator ?? new KnowledgeBaseValidator();
            _indexer = indexer ?? new KnowledgeBaseIndexer();
            _searcher = searcher ?? new KnowledgeBaseSearcher();
            _backupManager = backupManager ?? new KnowledgeBaseBackupManager();
            _migrationManager = migrationManager ?? new KnowledgeBaseMigrationManager();
            _securityManager = securityManager ?? new KnowledgeBaseSecurityManager();

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            _connection = new SqliteConnection();
            _connectionSemaphore = new SemaphoreSlim(
                _configuration.MaxConnections,
                _configuration.MaxConnections);

            _caches = new Dictionary<string, object>();

            InitializeCaches();

            SchemaVersion = new Version(1, 0, 0);
            Metadata = new KnowledgeBaseMetadata();
        }

        #endregion;

        #region Public Methods - Initialization and Lifecycle;

        /// <summary>
        /// Initializes the knowledge base asynchronously.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.Info("Initializing KnowledgeBase...");

                // Create required directories;
                CreateRequiredDirectories();

                // Set database file path;
                DatabaseFilePath = GetDatabaseFilePath();

                // Initialize security manager;
                await _securityManager.InitializeAsync(cancellationToken);

                // Initialize backup manager;
                await _backupManager.InitializeAsync(cancellationToken);

                // Initialize migration manager;
                await _migrationManager.InitializeAsync(cancellationToken);

                // Initialize indexer;
                await _indexer.InitializeAsync(cancellationToken);

                // Initialize searcher;
                await _searcher.InitializeAsync(cancellationToken);

                _isInitialized = true;
                _logger.Info("KnowledgeBase initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize KnowledgeBase: {ex.Message}", ex);
                throw new KnowledgeBaseInitializationException(
                    "Failed to initialize KnowledgeBase", ex);
            }
        }

        /// <summary>
        /// Opens the knowledge base database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task OpenAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (_isOpen)
                return;

            try
            {
                _logger.Info($"Opening knowledge base: {DatabaseFilePath}");

                // Create connection string;
                var connectionString = CreateConnectionString();

                // Open database connection;
                _connection.ConnectionString = connectionString;
                await _connection.OpenAsync(cancellationToken);

                // Enable extensions;
                await EnableExtensionsAsync(cancellationToken);

                // Apply pragma settings;
                await ApplyPragmaSettingsAsync(cancellationToken);

                // Check if database exists and initialize if needed;
                await InitializeDatabaseAsync(cancellationToken);

                // Load metadata;
                await LoadMetadataAsync(cancellationToken);

                // Build indexes if needed;
                await BuildIndexesAsync(cancellationToken);

                _isOpen = true;

                // Fire opened event;
                OnKnowledgeBaseOpened(new KnowledgeBaseOpenedEventArgs;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    SchemaVersion = SchemaVersion,
                    Metadata = Metadata,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Knowledge base opened successfully: {DatabaseFilePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to open knowledge base: {ex.Message}", ex);
                throw new KnowledgeBaseOpenException(
                    $"Failed to open knowledge base: {DatabaseFilePath}", ex);
            }
        }

        /// <summary>
        /// Closes the knowledge base database.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task CloseAsync()
        {
            ValidateNotDisposed();

            if (!_isOpen)
                return;

            try
            {
                _logger.Info("Closing knowledge base...");

                // Perform cleanup operations;
                await PerformCleanupAsync();

                // Close connection;
                if (_connection.State != ConnectionState.Closed)
                {
                    await _connection.CloseAsync();
                }

                _isOpen = false;

                // Fire closed event;
                OnKnowledgeBaseClosed(new KnowledgeBaseClosedEventArgs;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info("Knowledge base closed successfully.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to close knowledge base: {ex.Message}", ex);
                throw new KnowledgeBaseCloseException("Failed to close knowledge base", ex);
            }
        }

        /// <summary>
        /// Creates a new knowledge base database.
        /// </summary>
        /// <param name="metadata">The knowledge base metadata.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task CreateAsync(
            KnowledgeBaseMetadata metadata = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (_isOpen)
                throw new InvalidOperationException("Knowledge base is already open");

            try
            {
                _logger.Info($"Creating new knowledge base: {DatabaseFilePath}");

                // Delete existing database file if it exists;
                if (File.Exists(DatabaseFilePath))
                {
                    File.Delete(DatabaseFilePath);
                }

                // Open connection to create database;
                var connectionString = CreateConnectionString();
                using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken);

                // Create schema;
                await CreateSchemaAsync(connection, cancellationToken);

                // Set metadata;
                Metadata = metadata ?? new KnowledgeBaseMetadata;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = Path.GetFileNameWithoutExtension(DatabaseFilePath),
                    Description = "NEDA Knowledge Base",
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    Version = SchemaVersion.ToString(),
                    Tags = new List<string> { "knowledge", "data", "nuda" }
                };

                // Save metadata;
                await SaveMetadataAsync(connection, cancellationToken);

                await connection.CloseAsync();

                _logger.Info($"Knowledge base created successfully: {DatabaseFilePath}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create knowledge base: {ex.Message}", ex);
                throw new KnowledgeBaseCreateException(
                    $"Failed to create knowledge base: {DatabaseFilePath}", ex);
            }
        }

        /// <summary>
        /// Optimizes the knowledge base database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task OptimizeAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                _logger.Info("Optimizing knowledge base...");

                var stopwatch = Stopwatch.StartNew();

                // Vacuum database;
                await ExecuteNonQueryAsync("VACUUM", cancellationToken);

                // Analyze database;
                await ExecuteNonQueryAsync("ANALYZE", cancellationToken);

                // Rebuild indexes;
                await RebuildIndexesAsync(cancellationToken);

                // Update statistics;
                await UpdateStatisticsAsync(cancellationToken);

                stopwatch.Stop();
                _lastOptimizationTime = DateTime.UtcNow;

                // Fire optimized event;
                OnKnowledgeBaseOptimized(new KnowledgeBaseOptimizedEventArgs;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    OptimizationTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Knowledge base optimized in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to optimize knowledge base: {ex.Message}", ex);
                throw new KnowledgeBaseOptimizationException("Failed to optimize knowledge base", ex);
            }
        }

        /// <summary>
        /// Compacts the knowledge base database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<CompactionResult> CompactAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                _logger.Info("Compacting knowledge base...");

                var result = new CompactionResult;
                {
                    StartTime = DateTime.UtcNow,
                    DatabaseFilePath = DatabaseFilePath;
                };

                // Get initial size;
                var initialSize = new FileInfo(DatabaseFilePath).Length;
                result.InitialSize = initialSize;

                // Execute vacuum with aggressive settings;
                await ExecuteNonQueryAsync("PRAGMA auto_vacuum = INCREMENTAL", cancellationToken);
                await ExecuteNonQueryAsync("VACUUM", cancellationToken);

                // Get final size;
                var finalSize = new FileInfo(DatabaseFilePath).Length;
                result.FinalSize = finalSize;
                result.SpaceRecovered = initialSize - finalSize;

                // Calculate percentage;
                if (initialSize > 0)
                {
                    result.CompactionPercentage = (double)result.SpaceRecovered / initialSize * 100;
                }

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Success = true;

                _logger.Info($"Knowledge base compacted: {result.SpaceRecovered} bytes recovered ({result.CompactionPercentage:F1}%)");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to compact knowledge base: {ex.Message}", ex);
                throw new KnowledgeBaseCompactionException("Failed to compact knowledge base", ex);
            }
        }

        /// <summary>
        /// Verifies the integrity of the knowledge base database.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The integrity check result.</returns>
        public async Task<IntegrityCheckResult> VerifyIntegrityAsync(CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                _logger.Info("Verifying knowledge base integrity...");

                var result = new IntegrityCheckResult;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    CheckTime = DateTime.UtcNow;
                };

                // Check integrity using SQLite integrity_check;
                var integrityResult = await ExecuteScalarAsync<string>(
                    "PRAGMA integrity_check",
                    cancellationToken);

                result.IntegrityCheckResult = integrityResult;
                result.IsIntegrityValid = integrityResult == "ok";

                // Check foreign key constraints;
                var foreignKeyResult = await ExecuteScalarAsync<string>(
                    "PRAGMA foreign_key_check",
                    cancellationToken);

                result.ForeignKeyCheckResult = foreignKeyResult;
                result.AreForeignKeysValid = string.IsNullOrEmpty(foreignKeyResult);

                // Check quick integrity;
                var quickCheckResult = await ExecuteScalarAsync<string>(
                    "PRAGMA quick_check",
                    cancellationToken);

                result.QuickCheckResult = quickCheckResult;
                result.IsQuickCheckValid = quickCheckResult == "ok";

                // Get page count and size;
                var pageCount = await ExecuteScalarAsync<long>("PRAGMA page_count", cancellationToken);
                var pageSize = await ExecuteScalarAsync<long>("PRAGMA page_size", cancellationToken);

                result.PageCount = pageCount;
                result.PageSize = pageSize;
                result.TotalSize = pageCount * pageSize;

                // Check for corruption;
                try
                {
                    await ExecuteNonQueryAsync("BEGIN IMMEDIATE", cancellationToken);
                    await ExecuteNonQueryAsync("COMMIT", cancellationToken);
                    result.IsTransactionValid = true;
                }
                catch
                {
                    result.IsTransactionValid = false;
                    result.Errors.Add("Transaction test failed - possible database corruption");
                }

                result.IsValid = result.IsIntegrityValid &&
                               result.AreForeignKeysValid &&
                               result.IsQuickCheckValid &&
                               result.IsTransactionValid;

                _logger.Info($"Integrity check completed: {(result.IsValid ? "VALID" : "INVALID")}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to verify integrity: {ex.Message}", ex);
                throw new KnowledgeBaseIntegrityException("Failed to verify knowledge base integrity", ex);
            }
        }

        #endregion;

        #region Public Methods - Knowledge Items;

        /// <summary>
        /// Adds a knowledge item to the knowledge base.
        /// </summary>
        /// <param name="item">The knowledge item to add.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The added knowledge item with assigned ID.</returns>
        public async Task<KnowledgeItem> AddItemAsync(
            KnowledgeItem item,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (item == null)
                throw new ArgumentNullException(nameof(item));

            _validator.ValidateKnowledgeItem(item);

            await _securityManager.ValidateItemAccessAsync(item, KnowledgeBaseOperation.Create, cancellationToken);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Generate ID if not provided;
                if (string.IsNullOrEmpty(item.Id))
                {
                    item.Id = GenerateItemId(item);
                }

                // Set timestamps;
                item.Created = DateTime.UtcNow;
                item.Modified = DateTime.UtcNow;

                // Set version;
                item.Version = 1;

                // Serialize content if needed;
                var contentJson = item.Content != null ?
                    JsonSerializer.Serialize(item.Content, _jsonOptions) : null;

                // Serialize metadata;
                var metadataJson = item.Metadata != null ?
                    JsonSerializer.Serialize(item.Metadata, _jsonOptions) : null;

                // Build SQL query;
                var sql = @"
                    INSERT INTO KnowledgeItems; 
                    (Id, Title, Description, Content, ContentType, ContentJson, Category, Tags, 
                     Source, Author, Language, Priority, Relevance, Confidence, Status, 
                     MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived)
                    VALUES; 
                    (@Id, @Title, @Description, @Content, @ContentType, @ContentJson, @Category, @Tags, 
                     @Source, @Author, @Language, @Priority, @Relevance, @Confidence, @Status, 
                     @MetadataJson, @Version, @Created, @Modified, @Expires, @AccessCount, @IsArchived)";

                var parameters = new[]
                {
                    new SqliteParameter("@Id", item.Id),
                    new SqliteParameter("@Title", item.Title ?? (object)DBNull.Value),
                    new SqliteParameter("@Description", item.Description ?? (object)DBNull.Value),
                    new SqliteParameter("@Content", item.ContentText ?? (object)DBNull.Value),
                    new SqliteParameter("@ContentType", item.ContentType ?? "text/plain"),
                    new SqliteParameter("@ContentJson", contentJson ?? (object)DBNull.Value),
                    new SqliteParameter("@Category", item.Category ?? (object)DBNull.Value),
                    new SqliteParameter("@Tags", item.Tags != null ? string.Join(",", item.Tags) : (object)DBNull.Value),
                    new SqliteParameter("@Source", item.Source ?? (object)DBNull.Value),
                    new SqliteParameter("@Author", item.Author ?? (object)DBNull.Value),
                    new SqliteParameter("@Language", item.Language ?? "en"),
                    new SqliteParameter("@Priority", item.Priority),
                    new SqliteParameter("@Relevance", item.Relevance),
                    new SqliteParameter("@Confidence", item.Confidence),
                    new SqliteParameter("@Status", item.Status.ToString()),
                    new SqliteParameter("@MetadataJson", metadataJson ?? (object)DBNull.Value),
                    new SqliteParameter("@Version", item.Version),
                    new SqliteParameter("@Created", item.Created),
                    new SqliteParameter("@Modified", item.Modified),
                    new SqliteParameter("@Expires", item.Expires ?? (object)DBNull.Value),
                    new SqliteParameter("@AccessCount", item.AccessCount),
                    new SqliteParameter("@IsArchived", item.IsArchived)
                };

                // Execute query;
                await ExecuteNonQueryAsync(sql, parameters, cancellationToken);

                // Index the item;
                await _indexer.IndexItemAsync(item, cancellationToken);

                // Update statistics;
                Interlocked.Increment(ref _totalInserts);
                stopwatch.Stop();

                // Update metadata;
                Metadata.TotalItems++;
                Metadata.LastModified = DateTime.UtcNow;
                await UpdateMetadataAsync(cancellationToken);

                // Fire event;
                OnKnowledgeItemAdded(new KnowledgeItemAddedEventArgs;
                {
                    ItemId = item.Id,
                    Item = item,
                    Timestamp = DateTime.UtcNow,
                    ExecutionTime = stopwatch.Elapsed;
                });

                _logger.Info($"Knowledge item added: {item.Id} ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return item;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add knowledge item: {ex.Message}", ex);
                throw new KnowledgeItemAddException($"Failed to add knowledge item: {item.Title}", ex);
            }
        }

        /// <summary>
        /// Adds multiple knowledge items in a batch.
        /// </summary>
        /// <param name="items">The knowledge items to add.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The batch operation result.</returns>
        public async Task<BatchOperationResult> AddItemsAsync(
            IEnumerable<KnowledgeItem> items,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (items == null)
                throw new ArgumentNullException(nameof(items));

            var itemList = items.ToList();
            if (itemList.Count == 0)
                return new BatchOperationResult { Success = true };

            var result = new BatchOperationResult;
            {
                Operation = "AddItems",
                TotalItems = itemList.Count,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                _logger.Info($"Adding {itemList.Count} knowledge items in batch");

                // Use transaction for batch operations;
                await ExecuteNonQueryAsync("BEGIN TRANSACTION", cancellationToken);

                foreach (var item in itemList)
                {
                    try
                    {
                        _validator.ValidateKnowledgeItem(item);
                        await _securityManager.ValidateItemAccessAsync(item, KnowledgeBaseOperation.Create, cancellationToken);

                        // Generate ID if not provided;
                        if (string.IsNullOrEmpty(item.Id))
                        {
                            item.Id = GenerateItemId(item);
                        }

                        // Set timestamps;
                        item.Created = DateTime.UtcNow;
                        item.Modified = DateTime.UtcNow;
                        item.Version = 1;

                        // Serialize content;
                        var contentJson = item.Content != null ?
                            JsonSerializer.Serialize(item.Content, _jsonOptions) : null;

                        var metadataJson = item.Metadata != null ?
                            JsonSerializer.Serialize(item.Metadata, _jsonOptions) : null;

                        // Build SQL query;
                        var sql = @"
                            INSERT INTO KnowledgeItems; 
                            (Id, Title, Description, Content, ContentType, ContentJson, Category, Tags, 
                             Source, Author, Language, Priority, Relevance, Confidence, Status, 
                             MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived)
                            VALUES; 
                            (@Id, @Title, @Description, @Content, @ContentType, @ContentJson, @Category, @Tags, 
                             @Source, @Author, @Language, @Priority, @Relevance, @Confidence, @Status, 
                             @MetadataJson, @Version, @Created, @Modified, @Expires, @AccessCount, @IsArchived)";

                        var parameters = new[]
                        {
                            new SqliteParameter("@Id", item.Id),
                            new SqliteParameter("@Title", item.Title ?? (object)DBNull.Value),
                            new SqliteParameter("@Description", item.Description ?? (object)DBNull.Value),
                            new SqliteParameter("@Content", item.ContentText ?? (object)DBNull.Value),
                            new SqliteParameter("@ContentType", item.ContentType ?? "text/plain"),
                            new SqliteParameter("@ContentJson", contentJson ?? (object)DBNull.Value),
                            new SqliteParameter("@Category", item.Category ?? (object)DBNull.Value),
                            new SqliteParameter("@Tags", item.Tags != null ? string.Join(",", item.Tags) : (object)DBNull.Value),
                            new SqliteParameter("@Source", item.Source ?? (object)DBNull.Value),
                            new SqliteParameter("@Author", item.Author ?? (object)DBNull.Value),
                            new SqliteParameter("@Language", item.Language ?? "en"),
                            new SqliteParameter("@Priority", item.Priority),
                            new SqliteParameter("@Relevance", item.Relevance),
                            new SqliteParameter("@Confidence", item.Confidence),
                            new SqliteParameter("@Status", item.Status.ToString()),
                            new SqliteParameter("@MetadataJson", metadataJson ?? (object)DBNull.Value),
                            new SqliteParameter("@Version", item.Version),
                            new SqliteParameter("@Created", item.Created),
                            new SqliteParameter("@Modified", item.Modified),
                            new SqliteParameter("@Expires", item.Expires ?? (object)DBNull.Value),
                            new SqliteParameter("@AccessCount", item.AccessCount),
                            new SqliteParameter("@IsArchived", item.IsArchived)
                        };

                        await ExecuteNonQueryAsync(sql, parameters, cancellationToken);

                        // Index the item;
                        await _indexer.IndexItemAsync(item, cancellationToken);

                        result.SuccessfulItems++;
                        result.ProcessedItems.Add(item.Id);
                    }
                    catch (Exception ex)
                    {
                        result.FailedItems++;
                        result.Errors.Add($"Failed to add item {item.Title}: {ex.Message}");
                        _logger.Warn($"Failed to add item in batch: {item.Title} - {ex.Message}");
                    }
                }

                await ExecuteNonQueryAsync("COMMIT", cancellationToken);

                // Update statistics;
                Interlocked.Add(ref _totalInserts, result.SuccessfulItems);

                // Update metadata;
                Metadata.TotalItems += result.SuccessfulItems;
                Metadata.LastModified = DateTime.UtcNow;
                await UpdateMetadataAsync(cancellationToken);

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Success = result.FailedItems == 0;

                _logger.Info($"Batch add completed: {result.SuccessfulItems} successful, {result.FailedItems} failed in {result.Duration.TotalSeconds:F2}s");

                return result;
            }
            catch (Exception ex)
            {
                // Rollback on error;
                await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);

                result.EndTime = DateTime.UtcNow;
                result.Duration = result.EndTime - result.StartTime;
                result.Success = false;
                result.Errors.Add($"Batch operation failed: {ex.Message}");

                _logger.Error($"Batch add failed: {ex.Message}", ex);
                throw new KnowledgeItemBatchException("Failed to add knowledge items in batch", ex);
            }
        }

        /// <summary>
        /// Updates a knowledge item in the knowledge base.
        /// </summary>
        /// <param name="item">The updated knowledge item.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The updated knowledge item.</returns>
        public async Task<KnowledgeItem> UpdateItemAsync(
            KnowledgeItem item,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (item == null)
                throw new ArgumentNullException(nameof(item));

            if (string.IsNullOrEmpty(item.Id))
                throw new ArgumentException("Knowledge item ID cannot be null or empty", nameof(item));

            _validator.ValidateKnowledgeItem(item);

            await _securityManager.ValidateItemAccessAsync(item, KnowledgeBaseOperation.Update, cancellationToken);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Get existing item;
                var existingItem = await GetItemByIdAsync(item.Id, cancellationToken);
                if (existingItem == null)
                {
                    throw new KnowledgeItemNotFoundException($"Knowledge item not found: {item.Id}");
                }

                // Update timestamps;
                item.Modified = DateTime.UtcNow;
                item.Created = existingItem.Created;
                item.Version = existingItem.Version + 1;
                item.AccessCount = existingItem.AccessCount;

                // Serialize content if needed;
                var contentJson = item.Content != null ?
                    JsonSerializer.Serialize(item.Content, _jsonOptions) : null;

                // Serialize metadata;
                var metadataJson = item.Metadata != null ?
                    JsonSerializer.Serialize(item.Metadata, _jsonOptions) : null;

                // Build SQL query;
                var sql = @"
                    UPDATE KnowledgeItems SET;
                        Title = @Title,
                        Description = @Description,
                        Content = @Content,
                        ContentType = @ContentType,
                        ContentJson = @ContentJson,
                        Category = @Category,
                        Tags = @Tags,
                        Source = @Source,
                        Author = @Author,
                        Language = @Language,
                        Priority = @Priority,
                        Relevance = @Relevance,
                        Confidence = @Confidence,
                        Status = @Status,
                        MetadataJson = @MetadataJson,
                        Version = @Version,
                        Modified = @Modified,
                        Expires = @Expires,
                        IsArchived = @IsArchived;
                    WHERE Id = @Id";

                var parameters = new[]
                {
                    new SqliteParameter("@Id", item.Id),
                    new SqliteParameter("@Title", item.Title ?? (object)DBNull.Value),
                    new SqliteParameter("@Description", item.Description ?? (object)DBNull.Value),
                    new SqliteParameter("@Content", item.ContentText ?? (object)DBNull.Value),
                    new SqliteParameter("@ContentType", item.ContentType ?? "text/plain"),
                    new SqliteParameter("@ContentJson", contentJson ?? (object)DBNull.Value),
                    new SqliteParameter("@Category", item.Category ?? (object)DBNull.Value),
                    new SqliteParameter("@Tags", item.Tags != null ? string.Join(",", item.Tags) : (object)DBNull.Value),
                    new SqliteParameter("@Source", item.Source ?? (object)DBNull.Value),
                    new SqliteParameter("@Author", item.Author ?? (object)DBNull.Value),
                    new SqliteParameter("@Language", item.Language ?? "en"),
                    new SqliteParameter("@Priority", item.Priority),
                    new SqliteParameter("@Relevance", item.Relevance),
                    new SqliteParameter("@Confidence", item.Confidence),
                    new SqliteParameter("@Status", item.Status.ToString()),
                    new SqliteParameter("@MetadataJson", metadataJson ?? (object)DBNull.Value),
                    new SqliteParameter("@Version", item.Version),
                    new SqliteParameter("@Modified", item.Modified),
                    new SqliteParameter("@Expires", item.Expires ?? (object)DBNull.Value),
                    new SqliteParameter("@IsArchived", item.IsArchived)
                };

                // Execute query;
                var rowsAffected = await ExecuteNonQueryAsync(sql, parameters, cancellationToken);

                if (rowsAffected == 0)
                {
                    throw new KnowledgeItemNotFoundException($"Knowledge item not found for update: {item.Id}");
                }

                // Re-index the item;
                await _indexer.IndexItemAsync(item, cancellationToken);

                //
                // Update statistics;
                Interlocked.Increment(ref _totalUpdates);
                stopwatch.Stop();

                // Update metadata;
                Metadata.LastModified = DateTime.UtcNow;
                await UpdateMetadataAsync(cancellationToken);

                // Fire event;
                OnKnowledgeItemUpdated(new KnowledgeItemUpdatedEventArgs;
                {
                    ItemId = item.Id,
                    OldItem = existingItem,
                    NewItem = item,
                    Timestamp = DateTime.UtcNow,
                    ExecutionTime = stopwatch.Elapsed;
                });

                _logger.Info($"Knowledge item updated: {item.Id} ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return item;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update knowledge item: {ex.Message}", ex);
                throw new KnowledgeItemUpdateException($"Failed to update knowledge item: {item.Id}", ex);
            }
        }

        /// <summary>
        /// Deletes a knowledge item from the knowledge base.
        /// </summary>
        /// <param name="itemId">The ID of the item to delete.</param>
        /// <param name="permanent">True to permanently delete, false to archive.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True if the item was deleted, false otherwise.</returns>
        public async Task<bool> DeleteItemAsync(
            string itemId,
            bool permanent = false,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrEmpty(itemId))
                throw new ArgumentException("Item ID cannot be null or empty", nameof(itemId));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Get the item to validate access;
                var item = await GetItemByIdAsync(itemId, cancellationToken);
                if (item == null)
                {
                    return false;
                }

                await _securityManager.ValidateItemAccessAsync(item, KnowledgeBaseOperation.Delete, cancellationToken);

                bool result;

                if (permanent)
                {
                    // Permanent deletion;
                    var sql = "DELETE FROM KnowledgeItems WHERE Id = @Id";
                    var parameters = new[] { new SqliteParameter("@Id", itemId) };

                    result = await ExecuteNonQueryAsync(sql, parameters, cancellationToken) > 0;

                    // Remove from index;
                    await _indexer.RemoveItemFromIndexAsync(itemId, cancellationToken);

                    if (result)
                    {
                        // Update metadata;
                        Metadata.TotalItems--;
                        Metadata.LastModified = DateTime.UtcNow;
                        await UpdateMetadataAsync(cancellationToken);
                    }
                }
                else;
                {
                    // Soft delete (archive)
                    var sql = "UPDATE KnowledgeItems SET IsArchived = 1, Modified = @Modified WHERE Id = @Id";
                    var parameters = new[]
                    {
                        new SqliteParameter("@Id", itemId),
                        new SqliteParameter("@Modified", DateTime.UtcNow)
                    };

                    result = await ExecuteNonQueryAsync(sql, parameters, cancellationToken) > 0;
                }

                // Update statistics;
                Interlocked.Increment(ref _totalDeletes);
                stopwatch.Stop();

                // Fire event;
                OnKnowledgeItemDeleted(new KnowledgeItemDeletedEventArgs;
                {
                    ItemId = itemId,
                    Item = item,
                    Permanent = permanent,
                    Timestamp = DateTime.UtcNow,
                    ExecutionTime = stopwatch.Elapsed;
                });

                _logger.Info($"Knowledge item {(permanent ? "permanently deleted" : "archived")}: {itemId} ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to delete knowledge item: {ex.Message}", ex);
                throw new KnowledgeItemDeleteException($"Failed to delete knowledge item: {itemId}", ex);
            }
        }

        /// <summary>
        /// Gets a knowledge item by its ID.
        /// </summary>
        /// <param name="itemId">The ID of the item to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The knowledge item, or null if not found.</returns>
        public async Task<KnowledgeItem> GetItemByIdAsync(
            string itemId,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrEmpty(itemId))
                throw new ArgumentException("Item ID cannot be null or empty", nameof(itemId));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Check cache first;
                var cacheKey = $"item_{itemId}";
                if (TryGetFromCache<KnowledgeItem>(cacheKey, out var cachedItem))
                {
                    // Update access count asynchronously;
                    _ = Task.Run(() => IncrementAccessCountAsync(itemId));

                    // Fire query event;
                    OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                    {
                        QueryType = "GetItemById",
                        QueryText = itemId,
                        ExecutionTime = stopwatch.Elapsed,
                        Timestamp = DateTime.UtcNow,
                        IsFromCache = true;
                    });

                    return cachedItem;
                }

                var sql = @"
                    SELECT; 
                        Id, Title, Description, Content, ContentType, ContentJson, Category, Tags,
                        Source, Author, Language, Priority, Relevance, Confidence, Status,
                        MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived;
                    FROM KnowledgeItems; 
                    WHERE Id = @Id AND IsArchived = 0";

                var parameters = new[] { new SqliteParameter("@Id", itemId) };

                using var command = CreateCommand(sql, parameters);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    var item = MapReaderToKnowledgeItem(reader);

                    // Validate access;
                    await _securityManager.ValidateItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken);

                    // Add to cache;
                    AddToCache(cacheKey, item, TimeSpan.FromMinutes(_configuration.CacheDurationMinutes));

                    // Update access count;
                    await IncrementAccessCountAsync(itemId);

                    // Update query statistics;
                    Interlocked.Increment(ref _totalQueries);
                    Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                    // Fire query event;
                    OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                    {
                        QueryType = "GetItemById",
                        QueryText = itemId,
                        ExecutionTime = stopwatch.Elapsed,
                        Timestamp = DateTime.UtcNow,
                        IsFromCache = false;
                    });

                    _logger.Debug($"Retrieved knowledge item: {itemId} ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                    return item;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get knowledge item: {ex.Message}", ex);
                throw new KnowledgeItemRetrievalException($"Failed to get knowledge item: {itemId}", ex);
            }
        }

        /// <summary>
        /// Gets multiple knowledge items by their IDs.
        /// </summary>
        /// <param name="itemIds">The IDs of the items to retrieve.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of knowledge items.</returns>
        public async Task<IEnumerable<KnowledgeItem>> GetItemsByIdsAsync(
            IEnumerable<string> itemIds,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (itemIds == null)
                throw new ArgumentNullException(nameof(itemIds));

            var idList = itemIds.Distinct().ToList();
            if (idList.Count == 0)
                return Enumerable.Empty<KnowledgeItem>();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var results = new List<KnowledgeItem>();
                var idsToQuery = new List<string>();

                // Check cache first;
                foreach (var itemId in idList)
                {
                    var cacheKey = $"item_{itemId}";
                    if (TryGetFromCache<KnowledgeItem>(cacheKey, out var cachedItem))
                    {
                        results.Add(cachedItem);
                    }
                    else;
                    {
                        idsToQuery.Add(itemId);
                    }
                }

                // Query remaining items from database;
                if (idsToQuery.Count > 0)
                {
                    // Build parameterized query;
                    var parameters = new List<SqliteParameter>();
                    var paramNames = new List<string>();

                    for (int i = 0; i < idsToQuery.Count; i++)
                    {
                        var paramName = $"@Id{i}";
                        paramNames.Add(paramName);
                        parameters.Add(new SqliteParameter(paramName, idsToQuery[i]));
                    }

                    var sql = $@"
                        SELECT; 
                            Id, Title, Description, Content, ContentType, ContentJson, Category, Tags,
                            Source, Author, Language, Priority, Relevance, Confidence, Status,
                            MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived;
                        FROM KnowledgeItems; 
                        WHERE Id IN ({string.Join(",", paramNames)}) 
                          AND IsArchived = 0";

                    using var command = CreateCommand(sql, parameters.ToArray());
                    using var reader = await command.ExecuteReaderAsync(cancellationToken);

                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var item = MapReaderToKnowledgeItem(reader);

                        // Validate access;
                        await _securityManager.ValidateItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken);

                        // Add to cache;
                        var cacheKey = $"item_{item.Id}";
                        AddToCache(cacheKey, item, TimeSpan.FromMinutes(_configuration.CacheDurationMinutes));

                        results.Add(item);
                    }
                }

                // Update access counts for all items;
                _ = Task.Run(() => IncrementAccessCountsAsync(idList));

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                // Fire query event;
                OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                {
                    QueryType = "GetItemsByIds",
                    QueryText = string.Join(",", idList.Take(5)) + (idList.Count > 5 ? "..." : ""),
                    ExecutionTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = results.Count > 0 && idsToQuery.Count == 0;
                });

                _logger.Debug($"Retrieved {results.Count} knowledge items ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get knowledge items: {ex.Message}", ex);
                throw new KnowledgeItemRetrievalException($"Failed to get knowledge items", ex);
            }
        }

        /// <summary>
        /// Gets all knowledge items in the knowledge base.
        /// </summary>
        /// <param name="skip">Number of items to skip.</param>
        /// <param name="take">Number of items to take.</param>
        /// <param name="includeArchived">Whether to include archived items.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of knowledge items.</returns>
        public async Task<IEnumerable<KnowledgeItem>> GetAllItemsAsync(
            int skip = 0,
            int take = 100,
            bool includeArchived = false,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (take > _configuration.MaxQueryResults)
            {
                take = _configuration.MaxQueryResults;
                _logger.Warn($"Requested take ({take}) exceeds maximum, limiting to {_configuration.MaxQueryResults}");
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var sql = @"
                    SELECT; 
                        Id, Title, Description, Content, ContentType, ContentJson, Category, Tags,
                        Source, Author, Language, Priority, Relevance, Confidence, Status,
                        MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived;
                    FROM KnowledgeItems";

                if (!includeArchived)
                {
                    sql += " WHERE IsArchived = 0";
                }

                sql += " ORDER BY Modified DESC LIMIT @Take OFFSET @Skip";

                var parameters = new[]
                {
                    new SqliteParameter("@Skip", skip),
                    new SqliteParameter("@Take", take)
                };

                using var command = CreateCommand(sql, parameters);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                var results = new List<KnowledgeItem>();
                var itemsToValidate = new List<KnowledgeItem>();

                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = MapReaderToKnowledgeItem(reader);
                    results.Add(item);
                    itemsToValidate.Add(item);
                }

                // Validate access for all items;
                await _securityManager.ValidateItemsAccessAsync(itemsToValidate, KnowledgeBaseOperation.Read, cancellationToken);

                // Filter out items that user doesn't have access to;
                var accessibleItems = new List<KnowledgeItem>();
                for (int i = 0; i < results.Count; i++)
                {
                    if (await _securityManager.CheckItemAccessAsync(results[i], KnowledgeBaseOperation.Read, cancellationToken))
                    {
                        accessibleItems.Add(results[i]);
                    }
                }

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                // Fire query event;
                OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                {
                    QueryType = "GetAllItems",
                    QueryText = $"skip={skip}, take={take}, includeArchived={includeArchived}",
                    ExecutionTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = false;
                });

                _logger.Debug($"Retrieved {accessibleItems.Count} knowledge items ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return accessibleItems;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get all knowledge items: {ex.Message}", ex);
                throw new KnowledgeItemRetrievalException("Failed to get all knowledge items", ex);
            }
        }

        /// <summary>
        /// Gets the total count of knowledge items.
        /// </summary>
        /// <param name="includeArchived">Whether to include archived items.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The total count of items.</returns>
        public async Task<int> GetItemCountAsync(
            bool includeArchived = false,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var sql = "SELECT COUNT(*) FROM KnowledgeItems";
                if (!includeArchived)
                {
                    sql += " WHERE IsArchived = 0";
                }

                var count = await ExecuteScalarAsync<int>(sql, cancellationToken);

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                _logger.Debug($"Retrieved item count: {count} ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return count;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get item count: {ex.Message}", ex);
                throw new KnowledgeBaseQueryException("Failed to get item count", ex);
            }
        }

        #endregion;

        #region Public Methods - Search and Query;

        /// <summary>
        /// Searches knowledge items using full-text search.
        /// </summary>
        /// <param name="query">The search query.</param>
        /// <param name="options">Search options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Search results.</returns>
        public async Task<SearchResults> SearchAsync(
            string query,
            SearchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrWhiteSpace(query))
                return new SearchResults { Query = query };

            options ??= new SearchOptions();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Use the searcher to perform the search;
                var results = await _searcher.SearchAsync(
                    query,
                    options,
                    cancellationToken);

                // Filter results based on security;
                var filteredResults = new List<SearchResultItem>();
                var accessibleItems = new List<KnowledgeItem>();

                foreach (var result in results.Items)
                {
                    // Get the item to check access;
                    var item = await GetItemByIdAsync(result.ItemId, cancellationToken);
                    if (item != null && await _securityManager.CheckItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken))
                    {
                        filteredResults.Add(result);
                        accessibleItems.Add(item);
                    }
                }

                // Update search results;
                results.Items = filteredResults;
                results.TotalCount = filteredResults.Count;

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                // Fire query event;
                OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                {
                    QueryType = "Search",
                    QueryText = query,
                    ExecutionTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = false;
                });

                _logger.Debug($"Search completed: '{query}' found {results.TotalCount} items ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Search failed: {ex.Message}", ex);
                throw new KnowledgeBaseSearchException($"Search failed for query: {query}", ex);
            }
        }

        /// <summary>
        /// Performs an advanced search with complex criteria.
        /// </summary>
        /// <param name="criteria">The search criteria.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Search results.</returns>
        public async Task<SearchResults> AdvancedSearchAsync(
            SearchCriteria criteria,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (criteria == null)
                throw new ArgumentNullException(nameof(criteria));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Build the query based on criteria;
                var (sql, parameters) = BuildAdvancedSearchQuery(criteria);

                // Get total count;
                var countSql = $"SELECT COUNT(*) FROM ({sql}) AS subquery";
                var totalCount = await ExecuteScalarAsync<int>(countSql, parameters, cancellationToken);

                // Apply pagination;
                if (criteria.Skip.HasValue && criteria.Take.HasValue)
                {
                    sql += $" LIMIT {criteria.Take.Value} OFFSET {criteria.Skip.Value}";
                }

                // Execute query;
                var items = new List<KnowledgeItem>();
                using var command = CreateCommand(sql, parameters);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = MapReaderToKnowledgeItem(reader);
                    items.Add(item);
                }

                // Filter based on security;
                var accessibleItems = new List<KnowledgeItem>();
                foreach (var item in items)
                {
                    if (await _securityManager.CheckItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken))
                    {
                        accessibleItems.Add(item);
                    }
                }

                // Create search results;
                var results = new SearchResults;
                {
                    Query = criteria.ToString(),
                    Items = accessibleItems.Select(item => new SearchResultItem;
                    {
                        ItemId = item.Id,
                        Title = item.Title,
                        Description = item.Description,
                        Relevance = item.Relevance,
                        Confidence = item.Confidence,
                        Score = CalculateSearchScore(item, criteria),
                        Highlights = new List<string>()
                    }).ToList(),
                    TotalCount = totalCount,
                    ExecutionTime = stopwatch.Elapsed;
                };

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                // Fire query event;
                OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                {
                    QueryType = "AdvancedSearch",
                    QueryText = criteria.ToString(),
                    ExecutionTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = false;
                });

                _logger.Debug($"Advanced search completed: found {accessibleItems.Count} items ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return results;
            }
            catch (Exception ex)
            {
                _logger.Error($"Advanced search failed: {ex.Message}", ex);
                throw new KnowledgeBaseSearchException("Advanced search failed", ex);
            }
        }

        /// <summary>
        /// Performs a semantic search using vector embeddings.
        /// </summary>
        /// <param name="query">The semantic query.</param>
        /// <param name="options">Search options.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Semantic search results.</returns>
        public async Task<SemanticSearchResults> SemanticSearchAsync(
            string query,
            SemanticSearchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrWhiteSpace(query))
                return new SemanticSearchResults { Query = query };

            options ??= new SemanticSearchOptions();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Check if semantic search is enabled;
                if (!_configuration.EnableSemanticSearch)
                {
                    throw new KnowledgeBaseFeatureDisabledException("Semantic search is disabled");
                }

                // Generate embedding for the query;
                var queryEmbedding = await GenerateEmbeddingAsync(query, cancellationToken);
                if (queryEmbedding == null || queryEmbedding.Length == 0)
                {
                    throw new KnowledgeBaseSearchException("Failed to generate embedding for query");
                }

                // Search for similar embeddings;
                var similarItems = await FindSimilarEmbeddingsAsync(
                    queryEmbedding,
                    options.MaxResults,
                    options.MinSimilarity,
                    cancellationToken);

                // Filter based on security;
                var accessibleItems = new List<KnowledgeItem>();
                var results = new List<SemanticSearchResultItem>();

                foreach (var (item, similarity) in similarItems)
                {
                    if (await _securityManager.CheckItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken))
                    {
                        accessibleItems.Add(item);

                        results.Add(new SemanticSearchResultItem;
                        {
                            ItemId = item.Id,
                            Title = item.Title,
                            Description = item.Description,
                            Similarity = similarity,
                            Item = item;
                        });
                    }
                }

                // Sort by similarity;
                results = results.OrderByDescending(r => r.Similarity).ToList();

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                // Create semantic search results;
                var searchResults = new SemanticSearchResults;
                {
                    Query = query,
                    QueryEmbedding = queryEmbedding,
                    Items = results,
                    TotalCount = results.Count,
                    ExecutionTime = stopwatch.Elapsed;
                };

                // Fire query event;
                OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                {
                    QueryType = "SemanticSearch",
                    QueryText = query,
                    ExecutionTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = false;
                });

                _logger.Debug($"Semantic search completed: '{query}' found {results.Count} items ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return searchResults;
            }
            catch (Exception ex)
            {
                _logger.Error($"Semantic search failed: {ex.Message}", ex);
                throw new KnowledgeBaseSearchException($"Semantic search failed for query: {query}", ex);
            }
        }

        /// <summary>
        /// Executes a custom SQL query.
        /// </summary>
        /// <param name="sql">The SQL query to execute.</param>
        /// <param name="parameters">Query parameters.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Query results as data table.</returns>
        public async Task<DataTable> ExecuteQueryAsync(
            string sql,
            IEnumerable<SqliteParameter> parameters = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("SQL query cannot be null or empty", nameof(sql));

            await _securityManager.ValidateQueryAccessAsync(sql, KnowledgeBaseOperation.Read, cancellationToken);

            var stopwatch = Stopwatch.StartNew();

            try
            {
                using var command = CreateCommand(sql, parameters?.ToArray());
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                var dataTable = new DataTable();
                dataTable.Load(reader);

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                // Fire query event;
                OnQueryExecuted(new KnowledgeBaseQueryEventArgs;
                {
                    QueryType = "CustomQuery",
                    QueryText = sql.Length > 100 ? sql.Substring(0, 100) + "..." : sql,
                    ExecutionTime = stopwatch.Elapsed,
                    Timestamp = DateTime.UtcNow,
                    IsFromCache = false;
                });

                _logger.Debug($"Custom query executed: {dataTable.Rows.Count} rows returned ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return dataTable;
            }
            catch (Exception ex)
            {
                _logger.Error($"Custom query execution failed: {ex.Message}", ex);
                throw new KnowledgeBaseQueryException($"Failed to execute query: {sql}", ex);
            }
        }

        #endregion;

        #region Public Methods - Categories and Tags;

        /// <summary>
        /// Gets all categories in the knowledge base.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of categories with counts.</returns>
        public async Task<IEnumerable<CategoryInfo>> GetCategoriesAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var sql = @"
                    SELECT; 
                        Category,
                        COUNT(*) as ItemCount,
                        MIN(Created) as OldestItem,
                        MAX(Created) as NewestItem,
                        AVG(Priority) as AveragePriority,
                        AVG(Relevance) as AverageRelevance;
                    FROM KnowledgeItems; 
                    WHERE IsArchived = 0; 
                      AND Category IS NOT NULL; 
                      AND Category != ''
                    GROUP BY Category;
                    ORDER BY ItemCount DESC";

                var categories = new List<CategoryInfo>();
                using var command = CreateCommand(sql);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var category = new CategoryInfo;
                    {
                        Name = reader.GetString(0),
                        ItemCount = reader.GetInt32(1),
                        OldestItem = reader.IsDBNull(2) ? null : (DateTime?)reader.GetDateTime(2),
                        NewestItem = reader.IsDBNull(3) ? null : (DateTime?)reader.GetDateTime(3),
                        AveragePriority = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                        AverageRelevance = reader.IsDBNull(5) ? 0 : reader.GetDouble(5)
                    };

                    categories.Add(category);
                }

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                _logger.Debug($"Retrieved {categories.Count} categories ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return categories;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get categories: {ex.Message}", ex);
                throw new KnowledgeBaseQueryException("Failed to get categories", ex);
            }
        }

        /// <summary>
        /// Gets all tags in the knowledge base.
        /// </summary>
        /// <param name="minCount">Minimum occurrence count.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of tags with counts.</returns>
        public async Task<IEnumerable<TagInfo>> GetTagsAsync(
            int minCount = 1,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Since tags are stored as comma-separated string, we need to split them;
                // This is more efficiently done with a custom function or in-memory processing;
                var sql = "SELECT Tags FROM KnowledgeItems WHERE IsArchived = 0 AND Tags IS NOT NULL AND Tags != ''";

                var tagCounts = new Dictionary<string, int>();
                using var command = CreateCommand(sql);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var tagsString = reader.GetString(0);
                    if (!string.IsNullOrEmpty(tagsString))
                    {
                        var tags = tagsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                            .Select(t => t.Trim())
                                            .Where(t => !string.IsNullOrEmpty(t));

                        foreach (var tag in tags)
                        {
                            if (tagCounts.ContainsKey(tag))
                            {
                                tagCounts[tag]++;
                            }
                            else;
                            {
                                tagCounts[tag] = 1;
                            }
                        }
                    }
                }

                // Convert to TagInfo objects;
                var tags = tagCounts;
                    .Where(kvp => kvp.Value >= minCount)
                    .Select(kvp => new TagInfo;
                    {
                        Name = kvp.Key,
                        Count = kvp.Value;
                    })
                    .OrderByDescending(t => t.Count)
                    .ThenBy(t => t.Name)
                    .ToList();

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                _logger.Debug($"Retrieved {tags.Count} tags ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return tags;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get tags: {ex.Message}", ex);
                throw new KnowledgeBaseQueryException("Failed to get tags", ex);
            }
        }

        /// <summary>
        /// Gets items by category.
        /// </summary>
        /// <param name="category">The category name.</param>
        /// <param name="skip">Number of items to skip.</param>
        /// <param name="take">Number of items to take.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of knowledge items in the category.</returns>
        public async Task<IEnumerable<KnowledgeItem>> GetItemsByCategoryAsync(
            string category,
            int skip = 0,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrEmpty(category))
                throw new ArgumentException("Category cannot be null or empty", nameof(category));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var sql = @"
                    SELECT; 
                        Id, Title, Description, Content, ContentType, ContentJson, Category, Tags,
                        Source, Author, Language, Priority, Relevance, Confidence, Status,
                        MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived;
                    FROM KnowledgeItems; 
                    WHERE Category = @Category; 
                      AND IsArchived = 0;
                    ORDER BY Modified DESC;
                    LIMIT @Take OFFSET @Skip";

                var parameters = new[]
                {
                    new SqliteParameter("@Category", category),
                    new SqliteParameter("@Skip", skip),
                    new SqliteParameter("@Take", take)
                };

                var items = new List<KnowledgeItem>();
                using var command = CreateCommand(sql, parameters);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = MapReaderToKnowledgeItem(reader);

                    // Check access;
                    if (await _securityManager.CheckItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken))
                    {
                        items.Add(item);
                    }
                }

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                _logger.Debug($"Retrieved {items.Count} items for category '{category}' ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return items;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get items by category: {ex.Message}", ex);
                throw new KnowledgeBaseQueryException($"Failed to get items by category: {category}", ex);
            }
        }

        /// <summary>
        /// Gets items by tag.
        /// </summary>
        /// <param name="tag">The tag name.</param>
        /// <param name="skip">Number of items to skip.</param>
        /// <param name="take">Number of items to take.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of knowledge items with the tag.</returns>
        public async Task<IEnumerable<KnowledgeItem>> GetItemsByTagAsync(
            string tag,
            int skip = 0,
            int take = 100,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (string.IsNullOrEmpty(tag))
                throw new ArgumentException("Tag cannot be null or empty", nameof(tag));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                // For simple tag matching (tags stored as comma-separated string)
                var sql = @"
                    SELECT; 
                        Id, Title, Description, Content, ContentType, ContentJson, Category, Tags,
                        Source, Author, Language, Priority, Relevance, Confidence, Status,
                        MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived;
                    FROM KnowledgeItems; 
                    WHERE Tags LIKE @TagPattern; 
                      AND IsArchived = 0;
                    ORDER BY Modified DESC;
                    LIMIT @Take OFFSET @Skip";

                // Create pattern for LIKE query (matches tag at start, middle, or end of comma-separated list)
                var tagPattern = $"%{tag}%";

                var parameters = new[]
                {
                    new SqliteParameter("@TagPattern", tagPattern),
                    new SqliteParameter("@Skip", skip),
                    new SqliteParameter("@Take", take)
                };

                var items = new List<KnowledgeItem>();
                using var command = CreateCommand(sql, parameters);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var item = MapReaderToKnowledgeItem(reader);

                    // Verify exact tag match (since LIKE can match partial tags)
                    if (item.Tags != null && item.Tags.Contains(tag))
                    {
                        // Check access;
                        if (await _securityManager.CheckItemAccessAsync(item, KnowledgeBaseOperation.Read, cancellationToken))
                        {
                            items.Add(item);
                        }
                    }
                }

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                _logger.Debug($"Retrieved {items.Count} items for tag '{tag}' ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return items;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get items by tag: {ex.Message}", ex);
                throw new KnowledgeBaseQueryException($"Failed to get items by tag: {tag}", ex);
            }
        }

        #endregion;

        #region Public Methods - Statistics and Monitoring;

        /// <summary>
        /// Gets detailed statistics about the knowledge base.
        /// </summary>
        /// <returns>Knowledge base statistics.</returns>
        public KnowledgeBaseStatistics GetStatistics()
        {
            ValidateNotDisposed();

            try
            {
                var stats = new KnowledgeBaseStatistics;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    IsOpen = _isOpen,
                    IsInitialized = _isInitialized,
                    SchemaVersion = SchemaVersion,
                    TotalQueries = _totalQueries,
                    TotalInserts = _totalInserts,
                    TotalUpdates = _totalUpdates,
                    TotalDeletes = _totalDeletes,
                    AverageQueryTimeMs = AverageQueryTimeMs,
                    PeakMemoryUsageBytes = _peakMemoryUsage,
                    LastBackupTime = _lastBackupTime,
                    LastOptimizationTime = _lastOptimizationTime,
                    CacheSize = GetCacheSize(),
                    ConnectionCount = GetActiveConnectionCount(),
                    Timestamp = DateTime.UtcNow;
                };

                // Add metadata statistics;
                if (Metadata != null)
                {
                    stats.TotalItems = Metadata.TotalItems;
                    stats.LastModified = Metadata.LastModified;
                }

                return stats;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get statistics: {ex.Message}", ex);
                throw new KnowledgeBaseStatisticsException("Failed to get statistics", ex);
            }
        }

        /// <summary>
        /// Gets usage statistics for a specific time period.
        /// </summary>
        /// <param name="startDate">Start date.</param>
        /// <param name="endDate">End date.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Usage statistics.</returns>
        public async Task<UsageStatistics> GetUsageStatisticsAsync(
            DateTime startDate,
            DateTime endDate,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            var stopwatch = Stopwatch.StartNew();

            try
            {
                var sql = @"
                    SELECT; 
                        DATE(Created) as Date,
                        COUNT(*) as ItemsCreated,
                        SUM(CASE WHEN IsArchived = 1 THEN 1 ELSE 0 END) as ItemsArchived,
                        AVG(Priority) as AvgPriority,
                        AVG(Relevance) as AvgRelevance,
                        AVG(Confidence) as AvgConfidence;
                    FROM KnowledgeItems; 
                    WHERE Created BETWEEN @StartDate AND @EndDate;
                    GROUP BY DATE(Created)
                    ORDER BY Date";

                var parameters = new[]
                {
                    new SqliteParameter("@StartDate", startDate),
                    new SqliteParameter("@EndDate", endDate)
                };

                var dailyStats = new List<DailyUsageStat>();
                using var command = CreateCommand(sql, parameters);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var stat = new DailyUsageStat;
                    {
                        Date = reader.GetDateTime(0),
                        ItemsCreated = reader.GetInt32(1),
                        ItemsArchived = reader.GetInt32(2),
                        AveragePriority = reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                        AverageRelevance = reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                        AverageConfidence = reader.IsDBNull(5) ? 0 : reader.GetDouble(5)
                    };

                    dailyStats.Add(stat);
                }

                // Get category distribution;
                var categorySql = @"
                    SELECT; 
                        Category,
                        COUNT(*) as ItemCount;
                    FROM KnowledgeItems; 
                    WHERE Created BETWEEN @StartDate AND @EndDate;
                      AND Category IS NOT NULL; 
                    GROUP BY Category;
                    ORDER BY ItemCount DESC";

                var categoryStats = new Dictionary<string, int>();
                using var categoryCommand = CreateCommand(categorySql, parameters);
                using var categoryReader = await categoryCommand.ExecuteReaderAsync(cancellationToken);

                while (await categoryReader.ReadAsync(cancellationToken))
                {
                    categoryStats[categoryReader.GetString(0)] = categoryReader.GetInt32(1);
                }

                // Update query statistics;
                Interlocked.Increment(ref _totalQueries);
                Interlocked.Add(ref _totalQueryTimeMs, stopwatch.ElapsedMilliseconds);

                var usageStats = new UsageStatistics;
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    DailyStats = dailyStats,
                    CategoryDistribution = categoryStats,
                    TotalQueries = _totalQueries,
                    TotalInserts = _totalInserts,
                    TotalUpdates = _totalUpdates,
                    TotalDeletes = _totalDeletes,
                    AverageQueryTimeMs = AverageQueryTimeMs,
                    ExecutionTime = stopwatch.Elapsed;
                };

                _logger.Debug($"Retrieved usage statistics for {startDate:yyyy-MM-dd} to {endDate:yyyy-MM-dd} ({stopwatch.Elapsed.TotalMilliseconds}ms)");

                return usageStats;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get usage statistics: {ex.Message}", ex);
                throw new KnowledgeBaseStatisticsException("Failed to get usage statistics", ex);
            }
        }

        /// <summary>
        /// Gets performance metrics.
        /// </summary>
        /// <returns>Performance metrics.</returns>
        public async Task<PerformanceMetrics> GetPerformanceMetricsAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            try
            {
                var metrics = new PerformanceMetrics;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalQueries = _totalQueries,
                    AverageQueryTimeMs = AverageQueryTimeMs,
                    PeakMemoryUsageBytes = _peakMemoryUsage,
                    CacheHitRatio = CalculateCacheHitRatio(),
                    ActiveConnections = GetActiveConnectionCount()
                };

                // Get database performance metrics if open;
                if (_isOpen)
                {
                    try
                    {
                        // Get database size;
                        var pageCount = await ExecuteScalarAsync<long>("PRAGMA page_count", cancellationToken);
                        var pageSize = await ExecuteScalarAsync<long>("PRAGMA page_size", cancellationToken);
                        metrics.DatabaseSizeBytes = pageCount * pageSize;

                        // Get cache statistics;
                        var cacheSize = await ExecuteScalarAsync<long>("PRAGMA cache_size", cancellationToken);
                        metrics.DatabaseCacheSize = cacheSize;

                        // Get other performance stats;
                        metrics.IsAutoVacuumEnabled = (await ExecuteScalarAsync<long>("PRAGMA auto_vacuum", cancellationToken)) > 0;
                        metrics.JournalMode = await ExecuteScalarAsync<string>("PRAGMA journal_mode", cancellationToken);
                        metrics.SynchronousMode = await ExecuteScalarAsync<string>("PRAGMA synchronous", cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to get some performance metrics: {ex.Message}");
                    }
                }

                return metrics;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get performance metrics: {ex.Message}", ex);
                throw new KnowledgeBaseStatisticsException("Failed to get performance metrics", ex);
            }
        }

        /// <summary>
        /// Resets the statistics counters.
        /// </summary>
        public void ResetStatistics()
        {
            ValidateNotDisposed();

            lock (_syncLock)
            {
                _totalQueries = 0;
                _totalInserts = 0;
                _totalUpdates = 0;
                _totalDeletes = 0;
                _totalQueryTimeMs = 0;
                _peakMemoryUsage = 0;

                _logger.Info("Statistics counters have been reset");
            }
        }

        #endregion;

        #region Public Methods - Backup and Restore;

        /// <summary>
        /// Creates a backup of the knowledge base.
        /// </summary>
        /// <param name="backupPath">The backup file path (optional).</param>
        /// <param name="compressionLevel">The compression level.</param>
        /// <param name="includeIndexes">Whether to include indexes in backup.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The backup result.</returns>
        public async Task<BackupResult> BackupAsync(
            string backupPath = null,
            CompressionLevel compressionLevel = CompressionLevel.Optimal,
            bool includeIndexes = true,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                _logger.Info("Starting knowledge base backup...");

                var result = await _backupManager.BackupAsync(
                    _connection,
                    backupPath,
                    compressionLevel,
                    includeIndexes,
                    cancellationToken);

                _lastBackupTime = DateTime.UtcNow;

                // Fire backup event;
                OnKnowledgeBaseBackedUp(new KnowledgeBaseBackupEventArgs;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    BackupFilePath = result.BackupFilePath,
                    BackupSize = result.BackupSize,
                    CompressionLevel = compressionLevel,
                    Timestamp = DateTime.UtcNow,
                    Duration = result.Duration;
                });

                _logger.Info($"Backup completed successfully: {result.BackupFilePath} ({result.BackupSize} bytes)");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Backup failed: {ex.Message}", ex);
                throw new KnowledgeBaseBackupException("Failed to backup knowledge base", ex);
            }
        }

        /// <summary>
        /// Restores the knowledge base from a backup.
        /// </summary>
        /// <param name="backupFilePath">The backup file path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The restore result.</returns>
        public async Task<RestoreResult> RestoreAsync(
            string backupFilePath,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();

            if (!File.Exists(backupFilePath))
                throw new FileNotFoundException($"Backup file not found: {backupFilePath}");

            try
            {
                _logger.Info($"Restoring knowledge base from backup: {backupFilePath}");

                // Close current connection if open;
                if (_isOpen)
                {
                    await CloseAsync();
                }

                // Restore from backup;
                var result = await _backupManager.RestoreAsync(
                    backupFilePath,
                    DatabaseFilePath,
                    cancellationToken);

                // Re-open the knowledge base;
                await OpenAsync(cancellationToken);

                _logger.Info($"Restore completed successfully: {backupFilePath}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Restore failed: {ex.Message}", ex);
                throw new KnowledgeBaseRestoreException($"Failed to restore from backup: {backupFilePath}", ex);
            }
        }

        /// <summary>
        /// Exports the knowledge base to a file.
        /// </summary>
        /// <param name="exportPath">The export file path.</param>
        /// <param name="format">The export format.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The export result.</returns>
        public async Task<ExportResult> ExportAsync(
            string exportPath,
            ExportFormat format = ExportFormat.Json,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                _logger.Info($"Exporting knowledge base to {format} format: {exportPath}");

                var stopwatch = Stopwatch.StartNew();

                // Create export directory if it doesn't exist;
                var exportDir = Path.GetDirectoryName(exportPath);
                if (!string.IsNullOrEmpty(exportDir) && !Directory.Exists(exportDir))
                {
                    Directory.CreateDirectory(exportDir);
                }

                // Get all items to export;
                var items = await GetAllItemsAsync(0, int.MaxValue, false, cancellationToken);
                var itemList = items.ToList();

                var result = new ExportResult;
                {
                    ExportPath = exportPath,
                    Format = format,
                    ItemCount = itemList.Count,
                    StartTime = DateTime.UtcNow;
                };

                switch (format)
                {
                    case ExportFormat.Json:
                        await ExportToJsonAsync(exportPath, itemList, cancellationToken);
                        break;

                    case ExportFormat.Xml:
                        await ExportToXmlAsync(exportPath, itemList, cancellationToken);
                        break;

                    case ExportFormat.Csv:
                        await ExportToCsvAsync(exportPath, itemList, cancellationToken);
                        break;

                    default:
                        throw new NotSupportedException($"Export format {format} is not supported");
                }

                stopwatch.Stop();
                result.EndTime = DateTime.UtcNow;
                result.Duration = stopwatch.Elapsed;
                result.Success = true;

                // Get file size;
                if (File.Exists(exportPath))
                {
                    result.FileSize = new FileInfo(exportPath).Length;
                }

                _logger.Info($"Export completed: {itemList.Count} items exported to {exportPath} ({result.FileSize} bytes)");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Export failed: {ex.Message}", ex);
                throw new KnowledgeBaseExportException($"Failed to export knowledge base: {exportPath}", ex);
            }
        }

        /// <summary>
        /// Imports knowledge items from a file.
        /// </summary>
        /// <param name="importPath">The import file path.</param>
        /// <param name="format">The import format.</param>
        /// <param name="clearExisting">Whether to clear existing items before import.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The import result.</returns>
        public async Task<ImportResult> ImportAsync(
            string importPath,
            ImportFormat format = ImportFormat.Json,
            bool clearExisting = false,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (!File.Exists(importPath))
                throw new FileNotFoundException($"Import file not found: {importPath}");

            try
            {
                _logger.Info($"Importing knowledge base from {format} format: {importPath}");

                var stopwatch = Stopwatch.StartNew();

                // Clear existing items if requested;
                if (clearExisting)
                {
                    _logger.Warn("Clearing existing knowledge items before import");
                    await ClearAllItemsAsync(cancellationToken);
                }

                var result = new ImportResult;
                {
                    ImportPath = importPath,
                    Format = format,
                    StartTime = DateTime.UtcNow;
                };

                IEnumerable<KnowledgeItem> items;

                switch (format)
                {
                    case ImportFormat.Json:
                        items = await ImportFromJsonAsync(importPath, cancellationToken);
                        break;

                    case ImportFormat.Xml:
                        items = await ImportFromXmlAsync(importPath, cancellationToken);
                        break;

                    case ImportFormat.Csv:
                        items = await ImportFromCsvAsync(importPath, cancellationToken);
                        break;

                    default:
                        throw new NotSupportedException($"Import format {format} is not supported");
                }

                var itemList = items.ToList();
                result.TotalItems = itemList.Count;

                // Import items in batches;
                var batchSize = _configuration.BatchSize;
                for (int i = 0; i < itemList.Count; i += batchSize)
                {
                    var batch = itemList.Skip(i).Take(batchSize);
                    var batchResult = await AddItemsAsync(batch, cancellationToken);

                    result.SuccessfulItems += batchResult.SuccessfulItems;
                    result.FailedItems += batchResult.FailedItems;
                    result.Errors.AddRange(batchResult.Errors);

                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                }

                stopwatch.Stop();
                result.EndTime = DateTime.UtcNow;
                result.Duration = stopwatch.Elapsed;
                result.Success = result.FailedItems == 0 && !result.Cancelled;

                _logger.Info($"Import completed: {result.SuccessfulItems} items imported, {result.FailedItems} failed from {importPath}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Import failed: {ex.Message}", ex);
                throw new KnowledgeBaseImportException($"Failed to import knowledge base: {importPath}", ex);
            }
        }

        #endregion;

        #region Public Methods - Index Management;

        /// <summary>
        /// Rebuilds all indexes in the knowledge base.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The index rebuild result.</returns>
        public async Task<IndexRebuildResult> RebuildIndexesAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                _logger.Info("Rebuilding knowledge base indexes...");

                var stopwatch = Stopwatch.StartNew();

                var result = new IndexRebuildResult;
                {
                    StartTime = DateTime.UtcNow,
                    DatabaseFilePath = DatabaseFilePath;
                };

                // Drop existing indexes;
                await DropIndexesAsync(cancellationToken);

                // Get all items for re-indexing;
                var items = await GetAllItemsAsync(0, int.MaxValue, false, cancellationToken);
                var itemList = items.ToList();

                // Re-index all items;
                foreach (var item in itemList)
                {
                    try
                    {
                        await _indexer.IndexItemAsync(item, cancellationToken);
                        result.IndexedItems++;
                    }
                    catch (Exception ex)
                    {
                        result.FailedItems++;
                        result.Errors.Add($"Failed to index item {item.Id}: {ex.Message}");
                        _logger.Warn($"Failed to index item {item.Id}: {ex.Message}");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        break;
                    }
                }

                // Create full-text search index;
                if (_configuration.EnableFullTextSearch)
                {
                    await CreateFullTextSearchIndexAsync(cancellationToken);
                }

                // Create semantic search index;
                if (_configuration.EnableSemanticSearch)
                {
                    await CreateSemanticSearchIndexAsync(cancellationToken);
                }

                stopwatch.Stop();
                result.EndTime = DateTime.UtcNow;
                result.Duration = stopwatch.Elapsed;
                result.Success = result.FailedItems == 0 && !result.Cancelled;

                // Fire index built event;
                OnIndexBuilt(new IndexBuiltEventArgs;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    IndexedItems = result.IndexedItems,
                    Duration = result.Duration,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Index rebuild completed: {result.IndexedItems} items indexed in {result.Duration.TotalSeconds:F2}s");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to rebuild indexes: {ex.Message}", ex);
                throw new KnowledgeBaseIndexException("Failed to rebuild indexes", ex);
            }
        }

        /// <summary>
        /// Gets index statistics.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Index statistics.</returns>
        public async Task<IndexStatistics> GetIndexStatisticsAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                var stats = new IndexStatistics;
                {
                    Timestamp = DateTime.UtcNow,
                    DatabaseFilePath = DatabaseFilePath;
                };

                // Get full-text search index stats;
                if (_configuration.EnableFullTextSearch)
                {
                    try
                    {
                        var ftsStats = await _indexer.GetFullTextSearchStatisticsAsync(cancellationToken);
                        stats.FullTextSearchStats = ftsStats;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to get FTS statistics: {ex.Message}");
                    }
                }

                // Get semantic index stats;
                if (_configuration.EnableSemanticSearch)
                {
                    try
                    {
                        var semanticStats = await _indexer.GetSemanticIndexStatisticsAsync(cancellationToken);
                        stats.SemanticIndexStats = semanticStats;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to get semantic index statistics: {ex.Message}");
                    }
                }

                // Get general index info;
                var indexSql = @"
                    SELECT; 
                        name,
                        type,
                        tbl_name,
                        sql;
                    FROM sqlite_master; 
                    WHERE type = 'index' 
                      AND name NOT LIKE 'sqlite_%'
                    ORDER BY name";

                var indexes = new List<IndexInfo>();
                using var command = CreateCommand(indexSql);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                while (await reader.ReadAsync(cancellationToken))
                {
                    var indexInfo = new IndexInfo;
                    {
                        Name = reader.GetString(0),
                        Type = reader.GetString(1),
                        TableName = reader.GetString(2),
                        Sql = reader.GetString(3)
                    };

                    indexes.Add(indexInfo);
                }

                stats.Indexes = indexes;
                stats.TotalIndexes = indexes.Count;

                return stats;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get index statistics: {ex.Message}", ex);
                throw new KnowledgeBaseIndexException("Failed to get index statistics", ex);
            }
        }

        #endregion;

        #region Public Methods - Schema Management;

        /// <summary>
        /// Gets the database schema information.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The database schema.</returns>
        public async Task<DatabaseSchema> GetSchemaAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            try
            {
                var schema = new DatabaseSchema;
                {
                    DatabaseFilePath = DatabaseFilePath,
                    SchemaVersion = SchemaVersion,
                    Timestamp = DateTime.UtcNow;
                };

                // Get tables;
                var tablesSql = @"
                    SELECT; 
                        name,
                        type,
                        tbl_name,
                        rootpage,
                        sql;
                    FROM sqlite_master; 
                    WHERE type IN ('table', 'view')
                    ORDER BY type, name";

                var tables = new List<TableInfo>();
                using var tablesCommand = CreateCommand(tablesSql);
                using var tablesReader = await tablesCommand.ExecuteReaderAsync(cancellationToken);

                while (await tablesReader.ReadAsync(cancellationToken))
                {
                    var tableInfo = new TableInfo;
                    {
                        Name = tablesReader.GetString(0),
                        Type = tablesReader.GetString(1),
                        TableName = tablesReader.GetString(2),
                        RootPage = tablesReader.GetInt32(3),
                        Sql = tablesReader.GetString(4)
                    };

                    // Get columns for this table;
                    var columnsSql = $"PRAGMA table_info({tableInfo.Name})";
                    var columns = new List<ColumnInfo>();

                    using var columnsCommand = CreateCommand(columnsSql);
                    using var columnsReader = await columnsCommand.ExecuteReaderAsync(cancellationToken);

                    while (await columnsReader.ReadAsync(cancellationToken))
                    {
                        var columnInfo = new ColumnInfo;
                        {
                            Cid = columnsReader.GetInt32(0),
                            Name = columnsReader.GetString(1),
                            Type = columnsReader.GetString(2),
                            NotNull = columnsReader.GetInt32(3) == 1,
                            DefaultValue = columnsReader.IsDBNull(4) ? null : columnsReader.GetString(4),
                            IsPrimaryKey = columnsReader.GetInt32(5) == 1;
                        };

                        columns.Add(columnInfo);
                    }

                    tableInfo.Columns = columns;
                    tables.Add(tableInfo);
                }

                schema.Tables = tables;

                // Get indexes;
                var indexesSql = @"
                    SELECT; 
                        name,
                        type,
                        tbl_name,
                        rootpage,
                        sql;
                    FROM sqlite_master; 
                    WHERE type = 'index'
                    ORDER BY name";

                var indexes = new List<IndexInfo>();
                using var indexesCommand = CreateCommand(indexesSql);
                using var indexesReader = await indexesCommand.ExecuteReaderAsync(cancellationToken);

                while (await indexesReader.ReadAsync(cancellationToken))
                {
                    var indexInfo = new IndexInfo;
                    {
                        Name = indexesReader.GetString(0),
                        Type = indexesReader.GetString(1),
                        TableName = indexesReader.GetString(2),
                        RootPage = indexesReader.GetInt32(3),
                        Sql = indexesReader.GetString(4)
                    };

                    indexes.Add(indexInfo);
                }

                schema.Indexes = indexes;

                return schema;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get schema: {ex.Message}", ex);
                throw new KnowledgeBaseSchemaException("Failed to get database schema", ex);
            }
        }

        /// <summary>
        /// Migrates the database schema to a new version.
        /// </summary>
        /// <param name="targetVersion">The target schema version.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The migration result.</returns>
        public async Task<MigrationResult> MigrateSchemaAsync(
            Version targetVersion,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateOpen();

            if (targetVersion == null)
                throw new ArgumentNullException(nameof(targetVersion));

            try
            {
                _logger.Info($"Migrating schema from {SchemaVersion} to {targetVersion}");

                var result = await _migrationManager.MigrateAsync(
                    _connection,
                    SchemaVersion,
                    targetVersion,
                    cancellationToken);

                if (result.Success)
                {
                    // Update schema version;
                    SchemaVersion = targetVersion;

                    // Update metadata;
                    Metadata.Version = SchemaVersion.ToString();
                    Metadata.LastModified = DateTime.UtcNow;
                    await UpdateMetadataAsync(cancellationToken);

                    // Fire schema migrated event;
                    OnSchemaMigrated(new SchemaMigratedEventArgs;
                    {
                        DatabaseFilePath = DatabaseFilePath,
                        OldVersion = result.OldVersion,
                        NewVersion = result.NewVersion,
                        MigrationSteps = result.MigrationSteps,
                        Duration = result.Duration,
                        Timestamp = DateTime.UtcNow;
                    });
                }

                _logger.Info($"Schema migration {(result.Success ? "completed" : "failed")}: {SchemaVersion}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Schema migration failed: {ex.Message}", ex);
                throw new KnowledgeBaseMigrationException(
                    $"Failed to migrate schema from {SchemaVersion} to {targetVersion}", ex);
            }
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Disposes the knowledge base and releases all resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes the knowledge base and releases all resources.
        /// </summary>
        /// <param name="disposing">True if called from Dispose, false if from finalizer.</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                try
                {
                    // Close the knowledge base if open;
                    if (_isOpen)
                    {
                        CloseAsync().GetAwaiter().GetResult();
                    }

                    // Dispose managed resources;
                    _connectionSemaphore?.Dispose();
                    _connection?.Dispose();

                    // Dispose components;
                    (_indexer as IDisposable)?.Dispose();
                    (_searcher as IDisposable)?.Dispose();
                    (_backupManager as IDisposable)?.Dispose();
                    (_migrationManager as IDisposable)?.Dispose();
                    (_securityManager as IDisposable)?.Dispose();

                    // Clear caches;
                    ClearCaches();

                    _logger.Info("KnowledgeBase disposed");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Error during disposal: {ex.Message}", ex);
                }
            }

            _disposed = true;
        }

        #endregion;

        #region Private Helper Methods;

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(KnowledgeBase));
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Knowledge base is not initialized");
        }

        private void ValidateOpen()
        {
            if (!_isOpen)
                throw new InvalidOperationException("Knowledge base is not open");
        }

        private string GetDatabaseFilePath()
        {
            if (!string.IsNullOrEmpty(_configuration.DatabaseFilePath))
            {
                return _configuration.DatabaseFilePath;
            }

            var basePath = _configuration.BaseDirectory ??
                          Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEDA", "KnowledgeBase");

            return Path.Combine(basePath, DEFAULT_DATABASE_FILE);
        }

        private void CreateRequiredDirectories()
        {
            var databaseDir = Path.GetDirectoryName(DatabaseFilePath);
            if (!string.IsNullOrEmpty(databaseDir) && !Directory.Exists(databaseDir))
            {
                Directory.CreateDirectory(databaseDir);
            }

            var backupDir = _configuration.BackupDirectory;
            if (!string.IsNullOrEmpty(backupDir) && !Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            var indexDir = _configuration.IndexDirectory;
            if (!string.IsNullOrEmpty(indexDir) && !Directory.Exists(indexDir))
            {
                Directory.CreateDirectory(indexDir);
            }
        }

        private string CreateConnectionString()
        {
            var builder = new SqliteConnectionStringBuilder;
            {
                DataSource = DatabaseFilePath,
                Mode = _configuration.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Cache = _configuration.SharedCache ? SqliteCacheMode.Shared : SqliteCacheMode.Private;
            };

            if (_configuration.ConnectionTimeoutSeconds > 0)
            {
                builder.DefaultTimeout = _configuration.ConnectionTimeoutSeconds;
            }

            if (!string.IsNullOrEmpty(_configuration.Password))
            {
                builder.Password = _configuration.Password;
            }

            return builder.ToString();
        }

        private async Task EnableExtensionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Enable loading extensions if configured;
                if (_configuration.EnableExtensions)
                {
                    await ExecuteNonQueryAsync("SELECT load_extension('mod_spatialite')", cancellationToken);
                }
            }
            catch
            {
                // Extension loading might fail, that's OK;
            }
        }

        private async Task ApplyPragmaSettingsAsync(CancellationToken cancellationToken)
        {
            // Apply pragma settings for performance and reliability;
            await ExecuteNonQueryAsync("PRAGMA journal_mode = WAL", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA synchronous = NORMAL", cancellationToken);
            await ExecuteNonQueryAsync($"PRAGMA cache_size = {_configuration.CacheSizeMB * 1024}", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA foreign_keys = ON", cancellationToken);
            await ExecuteNonQueryAsync("PRAGMA temp_store = MEMORY", cancellationToken);

            if (_configuration.ReadOnly)
            {
                await ExecuteNonQueryAsync("PRAGMA query_only = ON", cancellationToken);
            }
        }

        private async Task InitializeDatabaseAsync(CancellationToken cancellationToken)
        {
            // Check if database exists;
            var databaseExists = File.Exists(DatabaseFilePath);

            if (!databaseExists)
            {
                // Create new database with schema;
                await CreateSchemaAsync(_connection, cancellationToken);

                // Initialize metadata;
                Metadata = new KnowledgeBaseMetadata;
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = Path.GetFileNameWithoutExtension(DatabaseFilePath),
                    Description = "NEDA Knowledge Base",
                    Created = DateTime.UtcNow,
                    Modified = DateTime.UtcNow,
                    Version = SchemaVersion.ToString(),
                    Tags = new List<string> { "knowledge", "data", "nuda" },
                    TotalItems = 0;
                };

                await SaveMetadataAsync(_connection, cancellationToken);

                _logger.Info($"Created new knowledge base database: {DatabaseFilePath}");
            }
            else;
            {
                // Check and migrate schema if needed;
                var currentVersion = await GetCurrentSchemaVersionAsync(cancellationToken);
                if (currentVersion != SchemaVersion)
                {
                    await MigrateSchemaAsync(SchemaVersion, cancellationToken);
                }
            }
        }

        private async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            var commands = new[]
            {
                // KnowledgeItems table;
                @"
                CREATE TABLE IF NOT EXISTS KnowledgeItems (
                    Id TEXT PRIMARY KEY,
                    Title TEXT,
                    Description TEXT,
                    Content TEXT,
                    ContentType TEXT DEFAULT 'text/plain',
                    ContentJson TEXT,
                    Category TEXT,
                    Tags TEXT,
                    Source TEXT,
                    Author TEXT,
                    Language TEXT DEFAULT 'en',
                    Priority REAL DEFAULT 0.5,
                    Relevance REAL DEFAULT 0.5,
                    Confidence REAL DEFAULT 0.5,
                    Status TEXT DEFAULT 'Active',
                    MetadataJson TEXT,
                    Version INTEGER DEFAULT 1,
                    Created DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Modified DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Expires DATETIME,
                    AccessCount INTEGER DEFAULT 0,
                    IsArchived INTEGER DEFAULT 0,
                    Embedding BLOB;
                )",
                
                // Create indexes;
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Category ON KnowledgeItems(Category)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Tags ON KnowledgeItems(Tags)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Priority ON KnowledgeItems(Priority)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Relevance ON KnowledgeItems(Relevance)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Status ON KnowledgeItems(Status)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Created ON KnowledgeItems(Created)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Modified ON KnowledgeItems(Modified)",
                "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_IsArchived ON KnowledgeItems(IsArchived)",
                
                // Create full-text search virtual table if enabled;
                _configuration.EnableFullTextSearch ? @"
                CREATE VIRTUAL TABLE IF NOT EXISTS KnowledgeItems_FTS; 
                USING fts5(
                    Title,
                    Description,
                    Content,
                    Category,
                    Tags,
                    Source,
                    Author,
                    content='KnowledgeItems',
                    content_rowid='rowid'
                )" : "",
                
                // Create triggers for FTS;
                _configuration.EnableFullTextSearch ? @"
                CREATE TRIGGER IF NOT EXISTS KnowledgeItems_AI AFTER INSERT ON KnowledgeItems BEGIN;
                    INSERT INTO KnowledgeItems_FTS(rowid, Title, Description, Content, Category, Tags, Source, Author)
                    VALUES (new.rowid, new.Title, new.Description, new.Content, new.Category, new.Tags, new.Source, new.Author);
                END" : "",

                _configuration.EnableFullTextSearch ? @"
                CREATE TRIGGER IF NOT EXISTS KnowledgeItems_AD AFTER DELETE ON KnowledgeItems BEGIN;
                    INSERT INTO KnowledgeItems_FTS(KnowledgeItems_FTS, rowid, Title, Description, Content, Category, Tags, Source, Author)
                    VALUES('delete', old.rowid, old.Title, old.Description, old.Content, old.Category, old.Tags, old.Source, old.Author);
                END" : "",

                _configuration.EnableFullTextSearch ? @"
                CREATE TRIGGER IF NOT EXISTS KnowledgeItems_AU AFTER UPDATE ON KnowledgeItems BEGIN;
                    INSERT INTO KnowledgeItems_FTS(KnowledgeItems_FTS, rowid, Title, Description, Content, Category, Tags, Source, Author)
                    VALUES('delete', old.rowid, old.Title, old.Description, old.Content, old.Category, old.Tags, old.Source, old.Author);
                    INSERT INTO KnowledgeItems_FTS(rowid, Title, Description, Content, Category, Tags, Source, Author)
                    VALUES (new.rowid, new.Title, new.Description, new.Content, new.Category, new.Tags, new.Source, new.Author);
                END" : "",
                
                // Metadata table;
                @"
                CREATE TABLE IF NOT EXISTS KnowledgeBaseMetadata (
                    Id TEXT PRIMARY KEY,
                    Name TEXT,
                    Description TEXT,
                    Version TEXT,
                    Created DATETIME,
                    Modified DATETIME,
                    Tags TEXT,
                    TotalItems INTEGER DEFAULT 0,
                    CustomData TEXT;
                )",
                
                // Statistics table;
                @"
                CREATE TABLE IF NOT EXISTS KnowledgeBaseStatistics (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp DATETIME,
                    TotalQueries INTEGER DEFAULT 0,
                    TotalInserts INTEGER DEFAULT 0,
                    TotalUpdates INTEGER DEFAULT 0,
                    TotalDeletes INTEGER DEFAULT 0,
                    AverageQueryTimeMs REAL DEFAULT 0,
                    PeakMemoryUsageBytes INTEGER DEFAULT 0;
                )",
                
                // Audit log table;
                @"
                CREATE TABLE IF NOT EXISTS AuditLog (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP,
                    Operation TEXT,
                    ItemId TEXT,
                    UserId TEXT,
                    Details TEXT,
                    IpAddress TEXT,
                    UserAgent TEXT;
                )"
            };

            foreach (var commandText in commands)
            {
                if (!string.IsNullOrWhiteSpace(commandText))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = commandText;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            _logger.Debug("Database schema created successfully");
        }

        private async Task<Version> GetCurrentSchemaVersionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var sql = @"
                    SELECT Version FROM KnowledgeBaseMetadata; 
                    WHERE Id = 'schema_version' 
                    LIMIT 1";

                var versionString = await ExecuteScalarAsync<string>(sql, cancellationToken);
                if (!string.IsNullOrEmpty(versionString))
                {
                    return Version.Parse(versionString);
                }
            }
            catch
            {
                // Table might not exist or be empty;
            }

            return new Version(1, 0, 0);
        }

        private async Task LoadMetadataAsync(CancellationToken cancellationToken)
        {
            try
            {
                var sql = "SELECT * FROM KnowledgeBaseMetadata LIMIT 1";
                using var command = CreateCommand(sql);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    Metadata = new KnowledgeBaseMetadata;
                    {
                        Id = reader.GetString(0),
                        Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                        Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Version = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Created = reader.GetDateTime(4),
                        Modified = reader.GetDateTime(5),
                        Tags = reader.IsDBNull(6) ? new List<string>() : reader.GetString(6).Split(',').ToList(),
                        TotalItems = reader.GetInt32(7),
                        CustomData = reader.IsDBNull(8) ? null : reader.GetString(8)
                    };

                    // Parse version;
                    if (!string.IsNullOrEmpty(Metadata.Version))
                    {
                        SchemaVersion = Version.Parse(Metadata.Version);
                    }
                }
                else;
                {
                    // Create default metadata;
                    Metadata = new KnowledgeBaseMetadata;
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = Path.GetFileNameWithoutExtension(DatabaseFilePath),
                        Description = "NEDA Knowledge Base",
                        Created = DateTime.UtcNow,
                        Modified = DateTime.UtcNow,
                        Version = SchemaVersion.ToString(),
                        Tags = new List<string> { "knowledge", "data", "nuda" },
                        TotalItems = 0;
                    };

                    await SaveMetadataAsync(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load metadata: {ex.Message}", ex);
                throw new KnowledgeBaseMetadataException("Failed to load knowledge base metadata", ex);
            }
        }

        private async Task SaveMetadataAsync(CancellationToken cancellationToken)
        {
            await SaveMetadataAsync(_connection, cancellationToken);
        }

        private async Task SaveMetadataAsync(SqliteConnection connection, CancellationToken cancellationToken)
        {
            try
            {
                var sql = @"
                    INSERT OR REPLACE INTO KnowledgeBaseMetadata; 
                    (Id, Name, Description, Version, Created, Modified, Tags, TotalItems, CustomData)
                    VALUES; 
                    (@Id, @Name, @Description, @Version, @Created, @Modified, @Tags, @TotalItems, @CustomData)";

                var parameters = new[]
                {
                    new SqliteParameter("@Id", Metadata.Id),
                    new SqliteParameter("@Name", Metadata.Name ?? (object)DBNull.Value),
                    new SqliteParameter("@Description", Metadata.Description ?? (object)DBNull.Value),
                    new SqliteParameter("@Version", Metadata.Version ?? SchemaVersion.ToString()),
                    new SqliteParameter("@Created", Metadata.Created),
                    new SqliteParameter("@Modified", Metadata.Modified),
                    new SqliteParameter("@Tags", Metadata.Tags != null ? string.Join(",", Metadata.Tags) : (object)DBNull.Value),
                    new SqliteParameter("@TotalItems", Metadata.TotalItems),
                    new SqliteParameter("@CustomData", Metadata.CustomData ?? (object)DBNull.Value)
                };

                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddRange(parameters);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save metadata: {ex.Message}", ex);
                throw new KnowledgeBaseMetadataException("Failed to save knowledge base metadata", ex);
            }
        }

        private async Task UpdateMetadataAsync(CancellationToken cancellationToken)
        {
            if (Metadata == null)
                return;

            try
            {
                Metadata.Modified = DateTime.UtcNow;

                // Update total items count;
                var totalItems = await GetItemCountAsync(false, cancellationToken);
                Metadata.TotalItems = totalItems;

                await SaveMetadataAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to update metadata: {ex.Message}");
            }
        }

        private async Task BuildIndexesAsync(CancellationToken cancellationToken)
        {
            if (!_configuration.AutoBuildIndexes)
                return;

            try
            {
                _logger.Info("Building indexes...");

                // Check if indexes need to be built;
                var indexExists = await CheckIndexExistsAsync("IX_KnowledgeItems_Category", cancellationToken);
                if (!indexExists)
                {
                    await RebuildIndexesAsync(cancellationToken);
                }
                else;
                {
                    // Update existing indexes if needed;
                    await UpdateIndexesAsync(cancellationToken);
                }

                _logger.Info("Indexes built successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to build indexes: {ex.Message}", ex);
            }
        }

        private async Task<bool> CheckIndexExistsAsync(string indexName, CancellationToken cancellationToken)
        {
            try
            {
                var sql = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @Name";
                var result = await ExecuteScalarAsync<int?>(sql, new[] { new SqliteParameter("@Name", indexName) }, cancellationToken);
                return result.HasValue;
            }
            catch
            {
                return false;
            }
        }

        private async Task UpdateIndexesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Check for items that need indexing;
                var sql = @"
                    SELECT COUNT(*) FROM KnowledgeItems; 
                    WHERE rowid > COALESCE(
                        (SELECT MAX(rowid) FROM KnowledgeItems_FTS), 
                        0;
                    )";

                var count = await ExecuteScalarAsync<int>(sql, cancellationToken);
                if (count > 0)
                {
                    _logger.Info($"Found {count} items that need indexing");

                    // Index new items;
                    var indexSql = @"
                        INSERT INTO KnowledgeItems_FTS(rowid, Title, Description, Content, Category, Tags, Source, Author)
                        SELECT rowid, Title, Description, Content, Category, Tags, Source, Author;
                        FROM KnowledgeItems; 
                        WHERE rowid > COALESCE(
                            (SELECT MAX(rowid) FROM KnowledgeItems_FTS), 
                            0;
                        )";

                    await ExecuteNonQueryAsync(indexSql, cancellationToken);
                    _logger.Info($"Indexed {count} new items");
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to update indexes: {ex.Message}");
            }
        }

        private async Task DropIndexesAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Get all index names;
                var sql = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%'";
                using var command = CreateCommand(sql);
                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                var indexes = new List<string>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    indexes.Add(reader.GetString(0));
                }

                // Drop indexes;
                foreach (var indexName in indexes)
                {
                    try
                    {
                        await ExecuteNonQueryAsync($"DROP INDEX IF EXISTS {indexName}", cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"Failed to drop index {indexName}: {ex.Message}");
                    }
                }

                // Drop FTS table if exists;
                if (_configuration.EnableFullTextSearch)
                {
                    await ExecuteNonQueryAsync("DROP TABLE IF EXISTS KnowledgeItems_FTS", cancellationToken);
                    await ExecuteNonQueryAsync("DROP TRIGGER IF EXISTS KnowledgeItems_AI", cancellationToken);
                    await ExecuteNonQueryAsync("DROP TRIGGER IF EXISTS KnowledgeItems_AD", cancellationToken);
                    await ExecuteNonQueryAsync("DROP TRIGGER IF EXISTS KnowledgeItems_AU", cancellationToken);
                }

                _logger.Debug($"Dropped {indexes.Count} indexes");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to drop indexes: {ex.Message}", ex);
                throw;
            }
        }

        private async Task CreateFullTextSearchIndexAsync(CancellationToken cancellationToken)
        {
            if (!_configuration.EnableFullTextSearch)
                return;

            try
            {
                // Create FTS virtual table;
                var sql = @"
                    CREATE VIRTUAL TABLE IF NOT EXISTS KnowledgeItems_FTS; 
                    USING fts5(
                        Title,
                        Description,
                        Content,
                        Category,
                        Tags,
                        Source,
                        Author,
                        content='KnowledgeItems',
                        content_rowid='rowid'
                    )";

                await ExecuteNonQueryAsync(sql, cancellationToken);

                // Create triggers;
                await ExecuteNonQueryAsync(@"
                    CREATE TRIGGER IF NOT EXISTS KnowledgeItems_AI AFTER INSERT ON KnowledgeItems BEGIN;
                        INSERT INTO KnowledgeItems_FTS(rowid, Title, Description, Content, Category, Tags, Source, Author)
                        VALUES (new.rowid, new.Title, new.Description, new.Content, new.Category, new.Tags, new.Source, new.Author);
                    END", cancellationToken);

                await ExecuteNonQueryAsync(@"
                    CREATE TRIGGER IF NOT EXISTS KnowledgeItems_AD AFTER DELETE ON KnowledgeItems BEGIN;
                        INSERT INTO KnowledgeItems_FTS(KnowledgeItems_FTS, rowid, Title, Description, Content, Category, Tags, Source, Author)
                        VALUES('delete', old.rowid, old.Title, old.Description, old.Content, old.Category, old.Tags, old.Source, old.Author);
                    END", cancellationToken);

                await ExecuteNonQueryAsync(@"
                    CREATE TRIGGER IF NOT EXISTS KnowledgeItems_AU AFTER UPDATE ON KnowledgeItems BEGIN;
                        INSERT INTO KnowledgeItems_FTS(KnowledgeItems_FTS, rowid, Title, Description, Content, Category, Tags, Source, Author)
                        VALUES('delete', old.rowid, old.Title, old.Description, old.Content, old.Category, old.Tags, old.Source, old.Author);
                        INSERT INTO KnowledgeItems_FTS(rowid, Title, Description, Content, Category, Tags, Source, Author)
                        VALUES (new.rowid, new.Title, new.Description, new.Content, new.Category, new.Tags, new.Source, new.Author);
                    END", cancellationToken);

                // Populate FTS table with existing data;
                await ExecuteNonQueryAsync(@"
                    INSERT INTO KnowledgeItems_FTS(rowid, Title, Description, Content, Category, Tags, Source, Author)
                    SELECT rowid, Title, Description, Content, Category, Tags, Source, Author;
                    FROM KnowledgeItems", cancellationToken);

                _logger.Info("Full-text search index created successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create full-text search index: {ex.Message}", ex);
                throw new KnowledgeBaseIndexException("Failed to create full-text search index", ex);
            }
        }

        private async Task CreateSemanticSearchIndexAsync(CancellationToken cancellationToken)
        {
            if (!_configuration.EnableSemanticSearch)
                return;

            try
            {
                // Create embedding index if needed;
                var sql = "CREATE INDEX IF NOT EXISTS IX_KnowledgeItems_Embedding ON KnowledgeItems(Embedding)";
                await ExecuteNonQueryAsync(sql, cancellationToken);

                _logger.Info("Semantic search index created successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create semantic search index: {ex.Message}", ex);
                throw new KnowledgeBaseIndexException("Failed to create semantic search index", ex);
            }
        }

        private async Task PerformCleanupAsync()
        {
            try
            {
                // Clean up expired items;
                if (_configuration.AutoCleanup)
                {
                    await CleanupExpiredItemsAsync();
                }

                // Clear caches;
                ClearCaches();

                // Update statistics;
                await UpdateStatisticsAsync();
            }
            catch (Exception ex)
            {
                _logger.Warn($"Cleanup failed: {ex.Message}");
            }
        }

        private async Task CleanupExpiredItemsAsync()
        {
            try
            {
                var sql = "DELETE FROM KnowledgeItems WHERE Expires IS NOT NULL AND Expires < @Now";
                var parameters = new[] { new SqliteParameter("@Now", DateTime.UtcNow) };

                var deletedCount = await ExecuteNonQueryAsync(sql, parameters);
                if (deletedCount > 0)
                {
                    _logger.Info($"Cleaned up {deletedCount} expired items");

                    // Update metadata;
                    Metadata.TotalItems -= deletedCount;
                    Metadata.LastModified = DateTime.UtcNow;
                    await UpdateMetadataAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to cleanup expired items: {ex.Message}");
            }
        }

        private async Task UpdateStatisticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var sql = @"
                    INSERT INTO KnowledgeBaseStatistics; 
                    (Timestamp, TotalQueries, TotalInserts, TotalUpdates, TotalDeletes, AverageQueryTimeMs, PeakMemoryUsageBytes)
                    VALUES; 
                    (@Timestamp, @TotalQueries, @TotalInserts, @TotalUpdates, @TotalDeletes, @AverageQueryTimeMs, @PeakMemoryUsageBytes)";

                var parameters = new[]
                {
                    new SqliteParameter("@Timestamp", DateTime.UtcNow),
                    new SqliteParameter("@TotalQueries", _totalQueries),
                    new SqliteParameter("@TotalInserts", _totalInserts),
                    new SqliteParameter("@TotalUpdates", _totalUpdates),
                    new SqliteParameter("@TotalDeletes", _totalDeletes),
                    new SqliteParameter("@AverageQueryTimeMs", AverageQueryTimeMs),
                    new SqliteParameter("@PeakMemoryUsageBytes", _peakMemoryUsage)
                };

                await ExecuteNonQueryAsync(sql, parameters, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to update statistics: {ex.Message}");
            }
        }

        private string GenerateItemId(KnowledgeItem item)
        {
            // Generate a unique ID based on content hash and timestamp;
            var contentToHash = $"{item.Title}|{item.Description}|{item.ContentText}|{DateTime.UtcNow.Ticks}";

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(contentToHash));
            var hashString = BitConverter.ToString(hash).Replace("-", "").ToLower();

            // Take first 32 characters for ID;
            return $"kb_{hashString.Substring(0, 32)}";
        }

        private KnowledgeItem MapReaderToKnowledgeItem(SqliteDataReader reader)
        {
            var item = new KnowledgeItem;
            {
                Id = reader.GetString(0),
                Title = reader.IsDBNull(1) ? null : reader.GetString(1),
                Description = reader.IsDBNull(2) ? null : reader.GetString(2),
                ContentText = reader.IsDBNull(3) ? null : reader.GetString(3),
                ContentType = reader.IsDBNull(4) ? "text/plain" : reader.GetString(4),
                Category = reader.IsDBNull(6) ? null : reader.GetString(6),
                Source = reader.IsDBNull(8) ? null : reader.GetString(8),
                Author = reader.IsDBNull(9) ? null : reader.GetString(9),
                Language = reader.IsDBNull(10) ? "en" : reader.GetString(10),
                Priority = reader.GetDouble(11),
                Relevance = reader.GetDouble(12),
                Confidence = reader.GetDouble(13),
                Status = Enum.TryParse<KnowledgeItemStatus>(reader.GetString(14), out var status) ? status : KnowledgeItemStatus.Active,
                Version = reader.GetInt32(16),
                Created = reader.GetDateTime(17),
                Modified = reader.GetDateTime(18),
                Expires = reader.IsDBNull(19) ? null : (DateTime?)reader.GetDateTime(19),
                AccessCount = reader.GetInt32(20),
                IsArchived = reader.GetBoolean(21)
            };

            // Parse tags;
            if (!reader.IsDBNull(7))
            {
                var tagsString = reader.GetString(7);
                item.Tags = tagsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                     .Select(t => t.Trim())
                                     .Where(t => !string.IsNullOrEmpty(t))
                                     .ToList();
            }

            // Parse content JSON;
            if (!reader.IsDBNull(5))
            {
                var contentJson = reader.GetString(5);
                try
                {
                    item.Content = JsonSerializer.Deserialize<object>(contentJson, _jsonOptions);
                }
                catch
                {
                    // If deserialization fails, content remains null;
                }
            }

            // Parse metadata JSON;
            if (!reader.IsDBNull(15))
            {
                var metadataJson = reader.GetString(15);
                try
                {
                    item.Metadata = JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson, _jsonOptions);
                }
                catch
                {
                    // If deserialization fails, metadata remains null;
                }
            }

            return item;
        }

        private async Task IncrementAccessCountAsync(string itemId)
        {
            try
            {
                var sql = "UPDATE KnowledgeItems SET AccessCount = AccessCount + 1 WHERE Id = @Id";
                var parameters = new[] { new SqliteParameter("@Id", itemId) };

                await ExecuteNonQueryAsync(sql, parameters);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to increment access count for item {itemId}: {ex.Message}");
            }
        }

        private async Task IncrementAccessCountsAsync(IEnumerable<string> itemIds)
        {
            try
            {
                var idList = itemIds.ToList();
                if (idList.Count == 0)
                    return;

                // Build parameterized query;
                var parameters = new List<SqliteParameter>();
                var paramNames = new List<string>();

                for (int i = 0; i < idList.Count; i++)
                {
                    var paramName = $"@Id{i}";
                    paramNames.Add(paramName);
                    parameters.Add(new SqliteParameter(paramName, idList[i]));
                }

                var sql = $@"
                    UPDATE KnowledgeItems; 
                    SET AccessCount = AccessCount + 1; 
                    WHERE Id IN ({string.Join(",", paramNames)})";

                await ExecuteNonQueryAsync(sql, parameters);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Failed to increment access counts: {ex.Message}");
            }
        }

        private (string sql, SqliteParameter[] parameters) BuildAdvancedSearchQuery(SearchCriteria criteria)
        {
            var conditions = new List<string>();
            var parameters = new List<SqliteParameter>();
            var paramIndex = 0;

            // Title condition;
            if (!string.IsNullOrEmpty(criteria.Title))
            {
                conditions.Add($"Title LIKE @Title{paramIndex}");
                parameters.Add(new SqliteParameter($"@Title{paramIndex}", $"%{criteria.Title}%"));
                paramIndex++;
            }

            // Description condition;
            if (!string.IsNullOrEmpty(criteria.Description))
            {
                conditions.Add($"Description LIKE @Description{paramIndex}");
                parameters.Add(new SqliteParameter($"@Description{paramIndex}", $"%{criteria.Description}%"));
                paramIndex++;
            }

            // Category condition;
            if (!string.IsNullOrEmpty(criteria.Category))
            {
                conditions.Add($"Category = @Category{paramIndex}");
                parameters.Add(new SqliteParameter($"@Category{paramIndex}", criteria.Category));
                paramIndex++;
            }

            // Tags condition;
            if (criteria.Tags != null && criteria.Tags.Any())
            {
                var tagConditions = new List<string>();
                foreach (var tag in criteria.Tags)
                {
                    tagConditions.Add($"Tags LIKE @Tag{paramIndex}");
                    parameters.Add(new SqliteParameter($"@Tag{paramIndex}", $"%{tag}%"));
                    paramIndex++;
                }
                conditions.Add($"({string.Join(" OR ", tagConditions)})");
            }

            // Priority range;
            if (criteria.MinPriority.HasValue)
            {
                conditions.Add($"Priority >= @MinPriority{paramIndex}");
                parameters.Add(new SqliteParameter($"@MinPriority{paramIndex}", criteria.MinPriority.Value));
                paramIndex++;
            }

            if (criteria.MaxPriority.HasValue)
            {
                conditions.Add($"Priority <= @MaxPriority{paramIndex}");
                parameters.Add(new SqliteParameter($"@MaxPriority{paramIndex}", criteria.MaxPriority.Value));
                paramIndex++;
            }

            // Relevance range;
            if (criteria.MinRelevance.HasValue)
            {
                conditions.Add($"Relevance >= @MinRelevance{paramIndex}");
                parameters.Add(new SqliteParameter($"@MinRelevance{paramIndex}", criteria.MinRelevance.Value));
                paramIndex++;
            }

            if (criteria.MaxRelevance.HasValue)
            {
                conditions.Add($"Relevance <= @MaxRelevance{paramIndex}");
                parameters.Add(new SqliteParameter($"@MaxRelevance{paramIndex}", criteria.MaxRelevance.Value));
                paramIndex++;
            }

            // Date range;
            if (criteria.FromDate.HasValue)
            {
                conditions.Add($"Created >= @FromDate{paramIndex}");
                parameters.Add(new SqliteParameter($"@FromDate{paramIndex}", criteria.FromDate.Value));
                paramIndex++;
            }

            if (criteria.ToDate.HasValue)
            {
                conditions.Add($"Created <= @ToDate{paramIndex}");
                parameters.Add(new SqliteParameter($"@ToDate{paramIndex}", criteria.ToDate.Value));
                paramIndex++;
            }

            // Status;
            if (criteria.Status.HasValue)
            {
                conditions.Add($"Status = @Status{paramIndex}");
                parameters.Add(new SqliteParameter($"@Status{paramIndex}", criteria.Status.Value.ToString()));
                paramIndex++;
            }

            // Archived flag;
            if (criteria.IncludeArchived.HasValue)
            {
                if (!criteria.IncludeArchived.Value)
                {
                    conditions.Add($"IsArchived = 0");
                }
            }
            else;
            {
                conditions.Add($"IsArchived = 0");
            }

            // Build SQL;
            var whereClause = conditions.Any() ? $"WHERE {string.Join(" AND ", conditions)}" : "";
            var sql = $@"
                SELECT; 
                    Id, Title, Description, Content, ContentType, ContentJson, Category, Tags,
                    Source, Author, Language, Priority, Relevance, Confidence, Status,
                    MetadataJson, Version, Created, Modified, Expires, AccessCount, IsArchived;
                FROM KnowledgeItems;
                {whereClause}
                ORDER BY {GetOrderByClause(criteria)}";

            return (sql, parameters.ToArray());
        }

        private string GetOrderByClause(SearchCriteria criteria)
        {
            if (string.IsNullOrEmpty(criteria.OrderBy))
                return "Modified DESC";

            var orderBy = criteria.OrderBy;
            var direction = criteria.OrderDescending ? "DESC" : "ASC";

            // Validate order by field;
            var validFields = new[] { "Title", "Created", "Modified", "Priority", "Relevance", "Confidence", "AccessCount" };
            if (!validFields.Contains(orderBy, StringComparer.OrdinalIgnoreCase))
            {
                orderBy = "Modified";
            }

            return $"{orderBy} {direction}";
        }

        private double CalculateSearchScore(KnowledgeItem item, SearchCriteria criteria)
        {
            double score = 0.0;

            // Base score from relevance and confidence;
            score += item.Relevance * 0.4;
            score += item.Confidence * 0.3;
            score += item.Priority * 0.2;

            // Boost for recent items;
            var daysOld = (DateTime.UtcNow - item.Modified).TotalDays;
            if (daysOld < 30)
            {
                score += (30 - daysOld) / 30 * 0.1;
            }

            return Math.Min(score, 1.0);
        }

        private async Task<byte[]> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken)
        {
            if (!_configuration.EnableSemanticSearch)
                return null;

            try
            {
                // Use the indexer to generate embedding;
                return await _indexer.GenerateEmbeddingAsync(text, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to generate embedding: {ex.Message}", ex);
                return null;
            }
        }

        private async Task<List<(KnowledgeItem Item, double Similarity)>> FindSimilarEmbeddingsAsync(
            byte[] queryEmbedding,
            int maxResults,
            double minSimilarity,
            CancellationToken cancellationToken)
        {
            if (queryEmbedding == null || queryEmbedding.Length == 0)
                return new List<(KnowledgeItem, double)>();

            try
            {
                // Use the indexer to find similar embeddings;
                return await _indexer.FindSimilarEmbeddingsAsync(
                    queryEmbedding,
                    maxResults,
                    minSimilarity,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to find similar embeddings: {ex.Message}", ex);
                return new List<(KnowledgeItem, double)>();
            }
        }

        private async Task ClearAllItemsAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Start transaction;
                await ExecuteNonQueryAsync("BEGIN TRANSACTION", cancellationToken);

                // Delete all items;
                await ExecuteNonQueryAsync("DELETE FROM KnowledgeItems", cancellationToken);

                // Clear FTS table if exists;
                if (_configuration.EnableFullTextSearch)
                {
                    await ExecuteNonQueryAsync("DELETE FROM KnowledgeItems_FTS", cancellationToken);
                }

                // Commit transaction;
                await ExecuteNonQueryAsync("COMMIT", cancellationToken);

                // Update metadata;
                Metadata.TotalItems = 0;
                Metadata.LastModified = DateTime.UtcNow;
                await UpdateMetadataAsync(cancellationToken);

                _logger.Info("Cleared all knowledge items");
            }
            catch (Exception ex)
            {
                // Rollback on error;
                await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);
                throw new KnowledgeBaseOperationException("Failed to clear all items", ex);
            }
        }

        private async Task ExportToJsonAsync(string exportPath, List<KnowledgeItem> items, CancellationToken cancellationToken)
        {
            var exportData = new;
            {
                Metadata = Metadata,
                ExportDate = DateTime.UtcNow,
                ItemCount = items.Count,
                Items = items;
            };

            var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            });

            await File.WriteAllTextAsync(exportPath, json, cancellationToken);
        }

        private async Task ExportToXmlAsync(string exportPath, List<KnowledgeItem> items, CancellationToken cancellationToken)
        {
            var exportData = new KnowledgeBaseExport;
            {
                Metadata = Metadata,
                ExportDate = DateTime.UtcNow,
                ItemCount = items.Count,
                Items = items;
            };

            var serializer = new XmlSerializer(typeof(KnowledgeBaseExport));
            using var writer = new StreamWriter(exportPath);
            serializer.Serialize(writer, exportData);
            await writer.FlushAsync();
        }

        private async Task ExportToCsvAsync(string exportPath, List<KnowledgeItem> items, CancellationToken cancellationToken)
        {
            using var writer = new StreamWriter(exportPath);

            // Write header;
            await writer.WriteLineAsync("Id,Title,Description,Category,Tags,Source,Author,Priority,Relevance,Confidence,Status,Created,Modified,AccessCount");

            // Write data;
            foreach (var item in items)
            {
                var line = $"{EscapeCsvField(item.Id)}," +
                          $"{EscapeCsvField(item.Title)}," +
                          $"{EscapeCsvField(item.Description)}," +
                          $"{EscapeCsvField(item.Category)}," +
                          $"{EscapeCsvField(item.Tags != null ? string.Join(";", item.Tags) : "")}," +
                          $"{EscapeCsvField(item.Source)}," +
                          $"{EscapeCsvField(item.Author)}," +
                          $"{item.Priority}," +
                          $"{item.Relevance}," +
                          $"{item.Confidence}," +
                          $"{item.Status}," +
                          $"{item.Created:yyyy-MM-dd HH:mm:ss}," +
                          $"{item.Modified:yyyy-MM-dd HH:mm:ss}," +
                          $"{item.AccessCount}";

                await writer.WriteLineAsync(line);
            }

            await writer.FlushAsync();
        }

        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
                return "";

            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }

            return field;
        }

        private async Task<IEnumerable<KnowledgeItem>> ImportFromJsonAsync(string importPath, CancellationToken cancellationToken)
        {
            var json = await File.ReadAllTextAsync(importPath, cancellationToken);
            var exportData = JsonSerializer.Deserialize<KnowledgeBaseExport>(json, _jsonOptions);

            return exportData?.Items ?? Enumerable.Empty<KnowledgeItem>();
        }

        private async Task<IEnumerable<KnowledgeItem>> ImportFromXmlAsync(string importPath, CancellationToken cancellationToken)
        {
            var serializer = new XmlSerializer(typeof(KnowledgeBaseExport));
            using var reader = new StreamReader(importPath);
            var exportData = (KnowledgeBaseExport)serializer.Deserialize(reader);

            return exportData?.Items ?? Enumerable.Empty<KnowledgeItem>();
        }

        private async Task<IEnumerable<KnowledgeItem>> ImportFromCsvAsync(string importPath, CancellationToken cancellationToken)
        {
            var items = new List<KnowledgeItem>();

            using var reader = new StreamReader(importPath);

            // Skip header;
            await reader.ReadLineAsync();

            string line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    var fields = ParseCsvLine(line);

                    if (fields.Length >= 14)
                    {
                        var item = new KnowledgeItem;
                        {
                            Id = fields[0],
                            Title = fields[1],
                            Description = fields[2],
                            Category = fields[3],
                            Tags = fields[4].Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
                            Source = fields[5],
                            Author = fields[6],
                            Priority = double.TryParse(fields[7], out var priority) ? priority : 0.5,
                            Relevance = double.TryParse(fields[8], out var relevance) ? relevance : 0.5,
                            Confidence = double.TryParse(fields[9], out var confidence) ? confidence : 0.5,
                            Status = Enum.TryParse<KnowledgeItemStatus>(fields[10], out var status) ? status : KnowledgeItemStatus.Active,
                            Created = DateTime.TryParse(fields[11], out var created) ? created : DateTime.UtcNow,
                            Modified = DateTime.TryParse(fields[12], out var modified) ? modified : DateTime.UtcNow,
                            AccessCount = int.TryParse(fields[13], out var accessCount) ? accessCount : 0;
                        };

                        items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn($"Failed to parse CSV line: {ex.Message}");
                }
            }

            return items;
        }

        private string[] ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var currentField = new StringBuilder();
            var inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var c = line[i];

                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        // Escaped quote;
                        currentField.Append('"');
                        i++;
                    }
                    else;
                    {
                        // Start or end of quoted field;
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == ',' && !inQuotes)
                {
                    // End of field;
                    fields.Add(currentField.ToString());
                    currentField.Clear();
                }
                else;
                {
                    currentField.Append(c);
                }
            }

            // Add last field;
            fields.Add(currentField.ToString());

            return fields.ToArray();
        }

        #endregion;

        #region Cache Management;

        private void InitializeCaches()
        {
            _caches["items"] = new LRUCache<string, KnowledgeItem>(_configuration.CacheSizeMB * 1024 * 1024);
            _caches["queries"] = new LRUCache<string, object>(_configuration.CacheSizeMB * 512 * 1024);
            _caches["metadata"] = new Dictionary<string, object>();
        }

        private void ClearCaches()
        {
            lock (_syncLock)
            {
                foreach (var cache in _caches.Values)
                {
                    if (cache is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    else if (cache is IDictionary dictionary)
                    {
                        dictionary.Clear();
                    }
                }

                _caches.Clear();
                InitializeCaches();
            }
        }

        private void AddToCache<T>(string key, T value, TimeSpan? expiration = null)
        {
            if (!_configuration.EnableCaching)
                return;

            lock (_syncLock)
            {
                if (_caches.TryGetValue("items", out var cacheObj) && cacheObj is LRUCache<string, KnowledgeItem> itemCache)
                {
                    if (value is KnowledgeItem item)
                    {
                        itemCache.Add(key, item);
                    }
                }
            }
        }

        private bool TryGetFromCache<T>(string key, out T value)
        {
            value = default;

            if (!_configuration.EnableCaching)
                return false;

            lock (_syncLock)
            {
                if (_caches.TryGetValue("items", out var cacheObj) && cacheObj is LRUCache<string, KnowledgeItem> itemCache)
                {
                    if (itemCache.TryGet(key, out var cachedItem) && cachedItem is T typedItem)
                    {
                        value = typedItem;
                        return true;
                    }
                }
            }

            return false;
        }

        private long GetCacheSize()
        {
            if (!_configuration.EnableCaching)
                return 0;

            long totalSize = 0;

            lock (_syncLock)
            {
                if (_caches.TryGetValue("items", out var cacheObj) && cacheObj is LRUCache<string, KnowledgeItem> itemCache)
                {
                    totalSize += itemCache.CurrentSize;
                }
            }

            return totalSize;
        }

        private double CalculateCacheHitRatio()
        {
            // This would require tracking cache hits/misses;
            // For now, return a placeholder value;
            return 0.75;
        }

        #endregion;

        #region Connection Management;

        private SqliteCommand CreateCommand(string sql, params SqliteParameter[] parameters)
        {
            var command = _connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = _configuration.QueryTimeoutSeconds;

            if (parameters != null && parameters.Length > 0)
            {
                command.Parameters.AddRange(parameters);
            }

            return command;
        }

        private async Task<int> ExecuteNonQueryAsync(string sql, CancellationToken cancellationToken = default)
        {
            return await ExecuteNonQueryAsync(sql, null, cancellationToken);
        }

        private async Task<int> ExecuteNonQueryAsync(string sql, SqliteParameter[] parameters, CancellationToken cancellationToken = default)
        {
            await _connectionSemaphore.WaitAsync(cancellationToken);

            try
            {
                using var command = CreateCommand(sql, parameters);
                return await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private async Task<T> ExecuteScalarAsync<T>(string sql, CancellationToken cancellationToken = default)
        {
            return await ExecuteScalarAsync<T>(sql, null, cancellationToken);
        }

        private async Task<T> ExecuteScalarAsync<T>(string sql, SqliteParameter[] parameters, CancellationToken cancellationToken = default)
        {
            await _connectionSemaphore.WaitAsync(cancellationToken);

            try
            {
                using var command = CreateCommand(sql, parameters);
                var result = await command.ExecuteScalarAsync(cancellationToken);

                if (result == DBNull.Value || result == null)
                {
                    return default;
                }

                return (T)Convert.ChangeType(result, typeof(T));
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        private int GetActiveConnectionCount()
        {
            return _configuration.MaxConnections - _connectionSemaphore.CurrentCount;
        }

        #endregion;

        #region Event Handlers;

        /// <summary>
        /// Raises the KnowledgeBaseOpened event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeBaseOpened(KnowledgeBaseOpenedEventArgs e)
        {
            KnowledgeBaseOpened?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the KnowledgeBaseClosed event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeBaseClosed(KnowledgeBaseClosedEventArgs e)
        {
            KnowledgeBaseClosed?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the KnowledgeItemAdded event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeItemAdded(KnowledgeItemAddedEventArgs e)
        {
            KnowledgeItemAdded?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the KnowledgeItemUpdated event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeItemUpdated(KnowledgeItemUpdatedEventArgs e)
        {
            KnowledgeItemUpdated?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the KnowledgeItemDeleted event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeItemDeleted(KnowledgeItemDeletedEventArgs e)
        {
            KnowledgeItemDeleted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the KnowledgeBaseBackedUp event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeBaseBackedUp(KnowledgeBaseBackupEventArgs e)
        {
            KnowledgeBaseBackedUp?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the KnowledgeBaseOptimized event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnKnowledgeBaseOptimized(KnowledgeBaseOptimizedEventArgs e)
        {
            KnowledgeBaseOptimized?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the QueryExecuted event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnQueryExecuted(KnowledgeBaseQueryEventArgs e)
        {
            QueryExecuted?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the IndexBuilt event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnIndexBuilt(IndexBuiltEventArgs e)
        {
            IndexBuilt?.Invoke(this, e);
        }

        /// <summary>
        /// Raises the SchemaMigrated event.
        /// </summary>
        /// <param name="e">Event arguments.</param>
        protected virtual void OnSchemaMigrated(SchemaMigratedEventArgs e)
        {
            SchemaMigrated?.Invoke(this, e);
        }

        #endregion;

        #region Nested Classes;

        /// <summary>
        /// Represents a least-recently-used cache.
        /// </summary>
        /// <typeparam name="TKey">The type of keys.</typeparam>
        /// <typeparam name="TValue">The type of values.</typeparam>
        private class LRUCache<TKey, TValue> : IDisposable where TValue : class;
        {
            private readonly int _maxSize;
            private readonly Dictionary<TKey, LinkedListNode<CacheItem>> _cacheMap;
            private readonly LinkedList<CacheItem> _lruList;
            private long _currentSize;

            public long CurrentSize => _currentSize;

            public LRUCache(int maxSize)
            {
                _maxSize = maxSize;
                _cacheMap = new Dictionary<TKey, LinkedListNode<CacheItem>>();
                _lruList = new LinkedList<CacheItem>();
            }

            public bool TryGet(TKey key, out TValue value)
            {
                lock (this)
                {
                    if (_cacheMap.TryGetValue(key, out var node))
                    {
                        // Move to front (most recently used)
                        _lruList.Remove(node);
                        _lruList.AddFirst(node);

                        value = node.Value.Value;
                        return true;
                    }
                }

                value = default;
                return false;
            }

            public void Add(TKey key, TValue value)
            {
                lock (this)
                {
                    if (_cacheMap.TryGetValue(key, out var existingNode))
                    {
                        // Update existing;
                        _lruList.Remove(existingNode);
                        _currentSize -= GetItemSize(existingNode.Value.Value);
                    }

                    var itemSize = GetItemSize(value);

                    // Evict if needed;
                    while (_currentSize + itemSize > _maxSize && _lruList.Count > 0)
                    {
                        var last = _lruList.Last;
                        _lruList.RemoveLast();
                        _cacheMap.Remove(last.Value.Key);
                        _currentSize -= GetItemSize(last.Value.Value);
                    }

                    // Add new item;
                    var newNode = new LinkedListNode<CacheItem>(new CacheItem(key, value));
                    _lruList.AddFirst(newNode);
                    _cacheMap[key] = newNode;
                    _currentSize += itemSize;
                }
            }

            private long GetItemSize(TValue value)
            {
                // Simple size estimation - can be improved;
                if (value == null)
                    return 0;

                // For KnowledgeItem, estimate size based on text length;
                if (value is KnowledgeItem item)
                {
                    long size = 0;
                    size += item.Id?.Length * 2 ?? 0;
                    size += item.Title?.Length * 2 ?? 0;
                    size += item.Description?.Length * 2 ?? 0;
                    size += item.ContentText?.Length * 2 ?? 0;
                    size += 100; // Overhead for other fields;
                    return size;
                }

                return 100; // Default overhead;
            }

            public void Dispose()
            {
                lock (this)
                {
                    _cacheMap.Clear();
                    _lruList.Clear();
                    _currentSize = 0;
                }
            }

            private class CacheItem;
            {
                public TKey Key { get; }
                public TValue Value { get; }

                public CacheItem(TKey key, TValue value)
                {
                    Key = key;
                    Value = value;
                }
            }
        }

        #endregion;
    }

    /// <summary>
    /// Extension class for KnowledgeBase.
    /// </summary>
    public static class KnowledgeBaseExtensions;
    {
        /// <summary>
        /// Gets a knowledge item by ID or creates a new one if not found.
        /// </summary>
        public static async Task<KnowledgeItem> GetOrCreateItemAsync(
            this KnowledgeBase knowledgeBase,
            string itemId,
            Func<KnowledgeItem> createFunc,
            CancellationToken cancellationToken = default)
        {
            var item = await knowledgeBase.GetItemByIdAsync(itemId, cancellationToken);
            if (item == null && createFunc != null)
            {
                item = createFunc();
                item.Id = itemId;
                item = await knowledgeBase.AddItemAsync(item, cancellationToken);
            }
            return item;
        }

        /// <summary>
        /// Performs a bulk update of knowledge items.
        /// </summary>
        public static async Task<BatchOperationResult> UpdateItemsAsync(
            this KnowledgeBase knowledgeBase,
            IEnumerable<KnowledgeItem> items,
            CancellationToken cancellationToken = default)
        {
            var result = new BatchOperationResult;
            {
                Operation = "UpdateItems",
                StartTime = DateTime.UtcNow;
            };

            var itemList = items.ToList();
            result.TotalItems = itemList.Count;

            foreach (var item in itemList)
            {
                try
                {
                    await knowledgeBase.UpdateItemAsync(item, cancellationToken);
                    result.SuccessfulItems++;
                }
                catch (Exception ex)
                {
                    result.FailedItems++;
                    result.Errors.Add($"Failed to update item {item.Id}: {ex.Message}");
                }
            }

            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            result.Success = result.FailedItems == 0;

            return result;
        }

        /// <summary>
        /// Searches for knowledge items with multiple queries and merges results.
        /// </summary>
        public static async Task<SearchResults> MultiSearchAsync(
            this KnowledgeBase knowledgeBase,
            IEnumerable<string> queries,
            SearchOptions options = null,
            CancellationToken cancellationToken = default)
        {
            var allResults = new List<SearchResultItem>();
            var seenIds = new HashSet<string>();

            foreach (var query in queries)
            {
                var results = await knowledgeBase.SearchAsync(query, options, cancellationToken);

                foreach (var result in results.Items)
                {
                    if (!seenIds.Contains(result.ItemId))
                    {
                        allResults.Add(result);
                        seenIds.Add(result.ItemId);
                    }
                }
            }

            return new SearchResults;
            {
                Query = string.Join(" OR ", queries),
                Items = allResults,
                TotalCount = allResults.Count,
                ExecutionTime = TimeSpan.Zero // Would need to track total time;
            };
        }
    }
}
#region Public Methods - Import/Export Helpers;

/// <summary>
/// Exports knowledge items to a JSON file.
/// </summary>
private async Task ExportToJsonAsync(string exportPath, List<KnowledgeItem> items, CancellationToken cancellationToken)
{
    var exportData = new KnowledgeBaseExport;
    {
        Metadata = Metadata,
        ExportDate = DateTime.UtcNow,
        ItemCount = items.Count,
        Items = items;
    };

    var json = JsonSerializer.Serialize(exportData, new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    });

    await File.WriteAllTextAsync(exportPath, json, cancellationToken);
}

/// <summary>
/// Imports knowledge items from a JSON file.
/// </summary>
private async Task<IEnumerable<KnowledgeItem>> ImportFromJsonAsync(string importPath, CancellationToken cancellationToken)
{
    var json = await File.ReadAllTextAsync(importPath, cancellationToken);
    var exportData = JsonSerializer.Deserialize<KnowledgeBaseExport>(json, _jsonOptions);

    return exportData?.Items ?? Enumerable.Empty<KnowledgeItem>();
}

/// <summary>
/// Exports knowledge items to an XML file.
/// </summary>
private async Task ExportToXmlAsync(string exportPath, List<KnowledgeItem> items, CancellationToken cancellationToken)
{
    var exportData = new KnowledgeBaseExport;
    {
        Metadata = Metadata,
        ExportDate = DateTime.UtcNow,
        ItemCount = items.Count,
        Items = items;
    };

    var serializer = new XmlSerializer(typeof(KnowledgeBaseExport));
    using var writer = new StreamWriter(exportPath);
    serializer.Serialize(writer, exportData);
    await writer.FlushAsync();
}

/// <summary>
/// Imports knowledge items from an XML file.
/// </summary>
private async Task<IEnumerable<KnowledgeItem>> ImportFromXmlAsync(string importPath, CancellationToken cancellationToken)
{
    var serializer = new XmlSerializer(typeof(KnowledgeBaseExport));
    using var reader = new StreamReader(importPath);
    var exportData = (KnowledgeBaseExport)serializer.Deserialize(reader);

    return exportData?.Items ?? Enumerable.Empty<KnowledgeItem>();
}

/// <summary>
/// Exports knowledge items to a CSV file.
/// </summary>
private async Task ExportToCsvAsync(string exportPath, List<KnowledgeItem> items, CancellationToken cancellationToken)
{
    using var writer = new StreamWriter(exportPath);

    // Write header;
    await writer.WriteLineAsync("Id,Title,Description,Content,Category,Tags,Source,Author,Language,Priority,Relevance,Confidence,Status,Created,Modified,Expires,AccessCount,IsArchived,Version");

    // Write data;
    foreach (var item in items)
    {
        var line = $"{EscapeCsvField(item.Id)}," +
                  $"{EscapeCsvField(item.Title)}," +
                  $"{EscapeCsvField(item.Description)}," +
                  $"{EscapeCsvField(item.ContentText)}," +
                  $"{EscapeCsvField(item.Category)}," +
                  $"{EscapeCsvField(item.Tags != null ? string.Join(";", item.Tags) : "")}," +
                  $"{EscapeCsvField(item.Source)}," +
                  $"{EscapeCsvField(item.Author)}," +
                  $"{EscapeCsvField(item.Language)}," +
                  $"{item.Priority}," +
                  $"{item.Relevance}," +
                  $"{item.Confidence}," +
                  $"{item.Status}," +
                  $"{item.Created:yyyy-MM-dd HH:mm:ss}," +
                  $"{item.Modified:yyyy-MM-dd HH:mm:ss}," +
                  $"{item.Expires?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""}," +
                  $"{item.AccessCount}," +
                  $"{item.IsArchived}," +
                  $"{item.Version}";

        await writer.WriteLineAsync(line);
    }

    await writer.FlushAsync();
}

/// <summary>
/// Imports knowledge items from a CSV file.
/// </summary>
private async Task<IEnumerable<KnowledgeItem>> ImportFromCsvAsync(string importPath, CancellationToken cancellationToken)
{
    var items = new List<KnowledgeItem>();

    using var reader = new StreamReader(importPath);

    // Skip header;
    await reader.ReadLineAsync();

    string line;
    while ((line = await reader.ReadLineAsync()) != null)
    {
        if (string.IsNullOrWhiteSpace(line))
            continue;

        try
        {
            var fields = ParseCsvLine(line);

            if (fields.Length >= 19)
            {
                var item = new KnowledgeItem;
                {
                    Id = fields[0],
                    Title = fields[1],
                    Description = fields[2],
                    ContentText = fields[3],
                    Category = fields[4],
                    Tags = fields[5].Split(';', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    Source = fields[6],
                    Author = fields[7],
                    Language = fields[8],
                    Priority = double.TryParse(fields[9], out var priority) ? priority : 0.5,
                    Relevance = double.TryParse(fields[10], out var relevance) ? relevance : 0.5,
                    Confidence = double.TryParse(fields[11], out var confidence) ? confidence : 0.5,
                    Status = Enum.TryParse<KnowledgeItemStatus>(fields[12], out var status) ? status : KnowledgeItemStatus.Active,
                    Created = DateTime.TryParse(fields[13], out var created) ? created : DateTime.UtcNow,
                    Modified = DateTime.TryParse(fields[14], out var modified) ? modified : DateTime.UtcNow,
                    Expires = DateTime.TryParse(fields[15], out var expires) ? expires : (DateTime?)null,
                    AccessCount = int.TryParse(fields[16], out var accessCount) ? accessCount : 0,
                    IsArchived = bool.TryParse(fields[17], out var isArchived) && isArchived,
                    Version = int.TryParse(fields[18], out var version) ? version : 1;
                };

                items.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"Failed to parse CSV line: {ex.Message}");
        }
    }

    return items;
}

private string EscapeCsvField(string field)
{
    if (string.IsNullOrEmpty(field))
        return "";

    if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
    {
        return $"\"{field.Replace("\"", "\"\"")}\"";
    }

    return field;
}

private string[] ParseCsvLine(string line)
{
    var fields = new List<string>();
    var currentField = new StringBuilder();
    var inQuotes = false;

    for (int i = 0; i < line.Length; i++)
    {
        var c = line[i];

        if (c == '"')
        {
            if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
            {
                // Escaped quote;
                currentField.Append('"');
                i++;
            }
            else;
            {
                // Start or end of quoted field;
                inQuotes = !inQuotes;
            }
        }
        else if (c == ',' && !inQuotes)
        {
            // End of field;
            fields.Add(currentField.ToString());
            currentField.Clear();
        }
        else;
        {
            currentField.Append(c);
        }
    }

    // Add last field;
    fields.Add(currentField.ToString());

    return fields.ToArray();
}

#endregion;

#region Public Methods - Batch Operations;

/// <summary>
/// Executes a batch of operations in a single transaction.
/// </summary>
public async Task<BatchOperationResult> ExecuteBatchAsync(
    IEnumerable<KnowledgeBaseOperation> operations,
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();
    ValidateOpen();

    var operationList = operations.ToList();
    if (operationList.Count == 0)
        return new BatchOperationResult { Success = true };

    var result = new BatchOperationResult;
    {
        Operation = "Batch",
        TotalItems = operationList.Count,
        StartTime = DateTime.UtcNow;
    };

    try
    {
        _logger.Info($"Executing batch of {operationList.Count} operations");

        await ExecuteNonQueryAsync("BEGIN TRANSACTION", cancellationToken);

        foreach (var op in operationList)
        {
            try
            {
                switch (op.Type)
                {
                    case KnowledgeBaseOperationType.Add:
                        if (op.Item != null)
                        {
                            await AddItemAsync(op.Item, cancellationToken);
                            result.SuccessfulItems++;
                        }
                        break;

                    case KnowledgeBaseOperationType.Update:
                        if (op.Item != null)
                        {
                            await UpdateItemAsync(op.Item, cancellationToken);
                            result.SuccessfulItems++;
                        }
                        break;

                    case KnowledgeBaseOperationType.Delete:
                        if (!string.IsNullOrEmpty(op.ItemId))
                        {
                            if (await DeleteItemAsync(op.ItemId, op.Permanent, cancellationToken))
                            {
                                result.SuccessfulItems++;
                            }
                        }
                        break;

                    case KnowledgeBaseOperationType.Custom:
                        if (!string.IsNullOrEmpty(op.CustomQuery))
                        {
                            await ExecuteNonQueryAsync(op.CustomQuery, op.Parameters, cancellationToken);
                            result.SuccessfulItems++;
                        }
                        break;
                }

                result.ProcessedItems.Add(op.Item?.Id ?? op.ItemId ?? "custom");
            }
            catch (Exception ex)
            {
                result.FailedItems++;
                result.Errors.Add($"Failed to execute operation: {ex.Message}");
                _logger.Warn($"Batch operation failed: {ex.Message}");
            }
        }

        await ExecuteNonQueryAsync("COMMIT", cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = result.FailedItems == 0;

        _logger.Info($"Batch execution completed: {result.SuccessfulItems} successful, {result.FailedItems} failed");

        return result;
    }
    catch (Exception ex)
    {
        await ExecuteNonQueryAsync("ROLLBACK", cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = false;
        result.Errors.Add($"Batch failed: {ex.Message}");

        _logger.Error($"Batch execution failed: {ex.Message}", ex);
        throw new KnowledgeBaseBatchException("Failed to execute batch operations", ex);
    }
}

/// <summary>
/// Performs a bulk update of item fields.
/// </summary>
public async Task<BulkUpdateResult> BulkUpdateAsync(
    string fieldName,
    object fieldValue,
    string whereClause = null,
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();
    ValidateOpen();

    var result = new BulkUpdateResult;
    {
        FieldName = fieldName,
        FieldValue = fieldValue,
        StartTime = DateTime.UtcNow;
    };

    try
    {
        var sql = $"UPDATE KnowledgeItems SET {fieldName} = @Value, Modified = @Modified";

        if (!string.IsNullOrEmpty(whereClause))
        {
            sql += $" WHERE {whereClause}";
        }

        var parameters = new[]
        {
                new SqliteParameter("@Value", fieldValue ?? DBNull.Value),
                new SqliteParameter("@Modified", DateTime.UtcNow)
            };

        result.RowsAffected = await ExecuteNonQueryAsync(sql, parameters, cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = true;

        // Update metadata;
        Metadata.LastModified = DateTime.UtcNow;
        await UpdateMetadataAsync(cancellationToken);

        _logger.Info($"Bulk update completed: {result.RowsAffected} rows updated for field {fieldName}");

        return result;
    }
    catch (Exception ex)
    {
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = false;
        result.Error = ex.Message;

        _logger.Error($"Bulk update failed: {ex.Message}", ex);
        throw new KnowledgeBaseOperationException($"Failed to perform bulk update on field {fieldName}", ex);
    }
}

#endregion;

#region Public Methods - Analytics;

/// <summary>
/// Gets content analysis for the knowledge base.
/// </summary>
public async Task<ContentAnalysis> AnalyzeContentAsync(
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();
    ValidateOpen();

    var stopwatch = Stopwatch.StartNew();

    try
    {
        var analysis = new ContentAnalysis;
        {
            AnalysisDate = DateTime.UtcNow,
            DatabaseFilePath = DatabaseFilePath,
            TotalItems = Metadata.TotalItems;
        };

        // Get word count statistics;
        var wordStatsSql = @"
                SELECT; 
                    AVG(LENGTH(Content) - LENGTH(REPLACE(Content, ' ', '')) + 1) as AvgWordCount,
                    MIN(LENGTH(Content) - LENGTH(REPLACE(Content, ' ', '')) + 1) as MinWordCount,
                    MAX(LENGTH(Content) - LENGTH(REPLACE(Content, ' ', '')) + 1) as MaxWordCount,
                    SUM(LENGTH(Content) - LENGTH(REPLACE(Content, ' ', '')) + 1) as TotalWordCount;
                FROM KnowledgeItems; 
                WHERE IsArchived = 0; 
                  AND Content IS NOT NULL; 
                  AND Content != ''";

        using var wordStatsCommand = CreateCommand(wordStatsSql);
        using var wordStatsReader = await wordStatsCommand.ExecuteReaderAsync(cancellationToken);

        if (await wordStatsReader.ReadAsync(cancellationToken))
        {
            analysis.AverageWordCount = wordStatsReader.IsDBNull(0) ? 0 : (int)wordStatsReader.GetDouble(0);
            analysis.MinWordCount = wordStatsReader.IsDBNull(1) ? 0 : wordStatsReader.GetInt32(1);
            analysis.MaxWordCount = wordStatsReader.IsDBNull(2) ? 0 : wordStatsReader.GetInt32(2);
            analysis.TotalWordCount = wordStatsReader.IsDBNull(3) ? 0 : wordStatsReader.GetInt64(3);
        }

        // Get content type distribution;
        var contentTypeSql = @"
                SELECT; 
                    ContentType,
                    COUNT(*) as Count;
                FROM KnowledgeItems; 
                WHERE IsArchived = 0; 
                  AND ContentType IS NOT NULL;
                GROUP BY ContentType;
                ORDER BY Count DESC";

        analysis.ContentTypeDistribution = new Dictionary<string, int>();
        using var contentTypeCommand = CreateCommand(contentTypeSql);
        using var contentTypeReader = await contentTypeCommand.ExecuteReaderAsync(cancellationToken);

        while (await contentTypeReader.ReadAsync(cancellationToken))
        {
            analysis.ContentTypeDistribution[contentTypeReader.GetString(0)] = contentTypeReader.GetInt32(1);
        }

        // Get language distribution;
        var languageSql = @"
                SELECT; 
                    Language,
                    COUNT(*) as Count;
                FROM KnowledgeItems; 
                WHERE IsArchived = 0; 
                  AND Language IS NOT NULL;
                GROUP BY Language;
                ORDER BY Count DESC";

        analysis.LanguageDistribution = new Dictionary<string, int>();
        using var languageCommand = CreateCommand(languageSql);
        using var languageReader = await languageCommand.ExecuteReaderAsync(cancellationToken);

        while (await languageReader.ReadAsync(cancellationToken))
        {
            analysis.LanguageDistribution[languageReader.GetString(0)] = languageReader.GetInt32(1);
        }

        // Get date-based statistics;
        var dateStatsSql = @"
                SELECT; 
                    DATE(Created) as Date,
                    COUNT(*) as ItemsCreated,
                    AVG(Priority) as AvgPriority,
                    AVG(Relevance) as AvgRelevance;
                FROM KnowledgeItems; 
                WHERE IsArchived = 0;
                GROUP BY DATE(Created)
                ORDER BY Date";

        analysis.DailyStats = new List<DailyContentStat>();
        using var dateStatsCommand = CreateCommand(dateStatsSql);
        using var dateStatsReader = await dateStatsCommand.ExecuteReaderAsync(cancellationToken);

        while (await dateStatsReader.ReadAsync(cancellationToken))
        {
            var stat = new DailyContentStat;
            {
                Date = dateStatsReader.GetDateTime(0),
                ItemsCreated = dateStatsReader.GetInt32(1),
                AveragePriority = dateStatsReader.IsDBNull(2) ? 0 : dateStatsReader.GetDouble(2),
                AverageRelevance = dateStatsReader.IsDBNull(3) ? 0 : dateStatsReader.GetDouble(3)
            };

            analysis.DailyStats.Add(stat);
        }

        // Calculate content density (items per MB)
        var databaseSize = new FileInfo(DatabaseFilePath).Length;
        if (databaseSize > 0)
        {
            analysis.ItemsPerMB = Metadata.TotalItems / (databaseSize / (1024.0 * 1024.0));
        }

        analysis.ExecutionTime = stopwatch.Elapsed;

        _logger.Info($"Content analysis completed in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return analysis;
    }
    catch (Exception ex)
    {
        _logger.Error($"Content analysis failed: {ex.Message}", ex);
        throw new KnowledgeBaseAnalyticsException("Failed to analyze content", ex);
    }
}

/// <summary>
/// Gets trend analysis for knowledge base growth.
/// </summary>
public async Task<TrendAnalysis> AnalyzeTrendsAsync(
    DateTime startDate,
    DateTime endDate,
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();
    ValidateOpen();

    var stopwatch = Stopwatch.StartNew();

    try
    {
        var analysis = new TrendAnalysis;
        {
            StartDate = startDate,
            EndDate = endDate,
            AnalysisDate = DateTime.UtcNow;
        };

        // Get daily growth;
        var growthSql = @"
                SELECT; 
                    DATE(Created) as Date,
                    COUNT(*) as NewItems,
                    SUM(CASE WHEN IsArchived = 1 THEN 1 ELSE 0 END) as ArchivedItems,
                    COUNT(DISTINCT Category) as UniqueCategories,
                    COUNT(DISTINCT Author) as UniqueAuthors;
                FROM KnowledgeItems; 
                WHERE Created BETWEEN @StartDate AND @EndDate;
                GROUP BY DATE(Created)
                ORDER BY Date";

        var parameters = new[]
        {
                new SqliteParameter("@StartDate", startDate),
                new SqliteParameter("@EndDate", endDate)
            };

        analysis.DailyGrowth = new List<DailyGrowth>();
        using var growthCommand = CreateCommand(growthSql, parameters);
        using var growthReader = await growthCommand.ExecuteReaderAsync(cancellationToken);

        while (await growthReader.ReadAsync(cancellationToken))
        {
            var growth = new DailyGrowth;
            {
                Date = growthReader.GetDateTime(0),
                NewItems = growthReader.GetInt32(1),
                ArchivedItems = growthReader.GetInt32(2),
                UniqueCategories = growthReader.GetInt32(3),
                UniqueAuthors = growthReader.GetInt32(4)
            };

            analysis.DailyGrowth.Add(growth);
        }

        // Calculate growth rates;
        if (analysis.DailyGrowth.Count > 1)
        {
            analysis.AverageDailyGrowth = analysis.DailyGrowth.Average(g => g.NewItems);
            analysis.PeakGrowthDay = analysis.DailyGrowth.MaxBy(g => g.NewItems)?.Date;
            analysis.PeakGrowthCount = analysis.DailyGrowth.Max(g => g.NewItems);

            // Calculate moving average (7-day)
            var movingAverages = new List<double>();
            for (int i = 6; i < analysis.DailyGrowth.Count; i++)
            {
                var average = analysis.DailyGrowth.Skip(i - 6).Take(7).Average(g => g.NewItems);
                movingAverages.Add(average);
            }

            analysis.SevenDayMovingAverage = movingAverages.Any() ? movingAverages.Average() : 0;
        }

        // Get category trends;
        var categoryTrendsSql = @"
                SELECT; 
                    Category,
                    DATE(Created) as Date,
                    COUNT(*) as ItemCount;
                FROM KnowledgeItems; 
                WHERE Created BETWEEN @StartDate AND @EndDate;
                  AND Category IS NOT NULL;
                GROUP BY Category, DATE(Created)
                ORDER BY Category, Date";

        var categoryTrends = new Dictionary<string, List<CategoryTrend>>();
        using var categoryTrendsCommand = CreateCommand(categoryTrendsSql, parameters);
        using var categoryTrendsReader = await categoryTrendsCommand.ExecuteReaderAsync(cancellationToken);

        while (await categoryTrendsReader.ReadAsync(cancellationToken))
        {
            var category = categoryTrendsReader.GetString(0);
            var date = categoryTrendsReader.GetDateTime(1);
            var count = categoryTrendsReader.GetInt32(2);

            if (!categoryTrends.ContainsKey(category))
            {
                categoryTrends[category] = new List<CategoryTrend>();
            }

            categoryTrends[category].Add(new CategoryTrend;
            {
                Date = date,
                ItemCount = count;
            });
        }

        analysis.CategoryTrends = categoryTrends;

        // Get tag trends;
        var tagTrendsSql = @"
                SELECT; 
                    Tags,
                    DATE(Created) as Date;
                FROM KnowledgeItems; 
                WHERE Created BETWEEN @StartDate AND @EndDate;
                  AND Tags IS NOT NULL;
                  AND Tags != ''";

        var tagTrends = new Dictionary<string, List<DateTime>>();
        using var tagTrendsCommand = CreateCommand(tagTrendsSql, parameters);
        using var tagTrendsReader = await tagTrendsCommand.ExecuteReaderAsync(cancellationToken);

        while (await tagTrendsReader.ReadAsync(cancellationToken))
        {
            var tagsString = tagTrendsReader.GetString(0);
            var date = tagTrendsReader.GetDateTime(1);

            var tags = tagsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(t => t.Trim())
                                .Where(t => !string.IsNullOrEmpty(t));

            foreach (var tag in tags)
            {
                if (!tagTrends.ContainsKey(tag))
                {
                    tagTrends[tag] = new List<DateTime>();
                }

                tagTrends[tag].Add(date);
            }
        }

        // Calculate tag frequency;
        analysis.TagTrends = tagTrends.ToDictionary(
            kvp => kvp.Key,
            kvp => new TagTrend;
            {
                TotalOccurrences = kvp.Value.Count,
                FirstSeen = kvp.Value.Min(),
                LastSeen = kvp.Value.Max(),
                AverageFrequency = kvp.Value.Count / ((endDate - startDate).TotalDays + 1)
            });

        analysis.ExecutionTime = stopwatch.Elapsed;

        _logger.Info($"Trend analysis completed in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return analysis;
    }
    catch (Exception ex)
    {
        _logger.Error($"Trend analysis failed: {ex.Message}", ex);
        throw new KnowledgeBaseAnalyticsException("Failed to analyze trends", ex);
    }
}

#endregion;

#region Public Methods - Maintenance;

/// <summary>
/// Performs maintenance operations on the knowledge base.
/// </summary>
public async Task<MaintenanceResult> PerformMaintenanceAsync(
    MaintenanceOptions options = null,
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();
    ValidateOpen();

    options ??= new MaintenanceOptions();

    var result = new MaintenanceResult;
    {
        StartTime = DateTime.UtcNow,
        DatabaseFilePath = DatabaseFilePath,
        Options = options;
    };

    try
    {
        _logger.Info("Starting knowledge base maintenance");

        // 1. Integrity check;
        if (options.CheckIntegrity)
        {
            result.IntegrityCheck = await VerifyIntegrityAsync(cancellationToken);
            result.Success = result.IntegrityCheck.IsValid;

            if (!result.Success)
            {
                _logger.Warn("Integrity check failed, aborting maintenance");
                return result;
            }
        }

        // 2. Backup if requested;
        if (options.CreateBackup)
        {
            try
            {
                result.BackupResult = await BackupAsync(
                    options.BackupPath,
                    options.BackupCompressionLevel,
                    options.IncludeIndexesInBackup,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Warn($"Backup failed: {ex.Message}");
                result.Errors.Add($"Backup failed: {ex.Message}");
            }
        }

        // 3. Clean up expired items;
        if (options.CleanupExpiredItems)
        {
            var cleanupSql = "DELETE FROM KnowledgeItems WHERE Expires IS NOT NULL AND Expires < @Now";
            var cleanupParams = new[] { new SqliteParameter("@Now", DateTime.UtcNow) };

            result.ExpiredItemsCleaned = await ExecuteNonQueryAsync(cleanupSql, cleanupParams, cancellationToken);
            _logger.Info($"Cleaned up {result.ExpiredItemsCleaned} expired items");
        }

        // 4. Archive old items;
        if (options.ArchiveOldItems && options.ArchiveThresholdDays.HasValue)
        {
            var thresholdDate = DateTime.UtcNow.AddDays(-options.ArchiveThresholdDays.Value);
            var archiveSql = @"
                    UPDATE KnowledgeItems; 
                    SET IsArchived = 1, Modified = @Modified;
                    WHERE Created < @Threshold; 
                      AND IsArchived = 0;
                      AND Status != 'Active'";

            var archiveParams = new[]
            {
                    new SqliteParameter("@Threshold", thresholdDate),
                    new SqliteParameter("@Modified", DateTime.UtcNow)
                };

            result.ItemsArchived = await ExecuteNonQueryAsync(archiveSql, archiveParams, cancellationToken);
            _logger.Info($"Archived {result.ItemsArchived} old items");
        }

        // 5. Update statistics;
        if (options.UpdateStatistics)
        {
            await UpdateStatisticsAsync(cancellationToken);
            _logger.Info("Statistics updated");
        }

        // 6. Rebuild indexes;
        if (options.RebuildIndexes)
        {
            var indexResult = await RebuildIndexesAsync(cancellationToken);
            result.IndexRebuildResult = indexResult;
            _logger.Info($"Indexes rebuilt: {indexResult.IndexedItems} items indexed");
        }

        // 7. Optimize database;
        if (options.OptimizeDatabase)
        {
            await OptimizeAsync(cancellationToken);
            _logger.Info("Database optimized");
        }

        // 8. Compact database;
        if (options.CompactDatabase)
        {
            var compactionResult = await CompactAsync(cancellationToken);
            result.CompactionResult = compactionResult;
            _logger.Info($"Database compacted: {compactionResult.SpaceRecovered} bytes recovered");
        }

        // 9. Update metadata;
        await UpdateMetadataAsync(cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = true;

        _logger.Info($"Maintenance completed in {result.Duration.TotalSeconds:F2}s");

        return result;
    }
    catch (Exception ex)
    {
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = false;
        result.Errors.Add($"Maintenance failed: {ex.Message}");

        _logger.Error($"Maintenance failed: {ex.Message}", ex);
        throw new KnowledgeBaseMaintenanceException("Failed to perform maintenance", ex);
    }
}

/// <summary>
/// Repairs the knowledge base database.
/// </summary>
public async Task<RepairResult> RepairAsync(
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();

    var result = new RepairResult;
    {
        StartTime = DateTime.UtcNow,
        DatabaseFilePath = DatabaseFilePath;
    };

    try
    {
        _logger.Info("Starting knowledge base repair");

        // 1. Close the database if open;
        if (_isOpen)
        {
            await CloseAsync();
        }

        // 2. Create backup before repair;
        var backupPath = Path.Combine(
            Path.GetDirectoryName(DatabaseFilePath),
            $"{Path.GetFileNameWithoutExtension(DatabaseFilePath)}_repair_backup_{DateTime.Now:yyyyMMdd_HHmmss}.db");

        File.Copy(DatabaseFilePath, backupPath, true);
        result.BackupCreated = true;
        result.BackupPath = backupPath;

        // 3. Use SQLite repair tool if available;
        var repairDbPath = DatabaseFilePath + ".repaired";

        try
        {
            // Try to use SQLite's .recover command via command line;
            var processStartInfo = new ProcessStartInfo;
            {
                FileName = "sqlite3",
                Arguments = $"\"{DatabaseFilePath}\" \".recover\" | sqlite3 \"{repairDbPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true;
            };

            using var process = Process.Start(processStartInfo);
            if (process != null)
            {
                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode == 0 && File.Exists(repairDbPath))
                {
                    // Replace original with repaired version;
                    File.Delete(DatabaseFilePath);
                    File.Move(repairDbPath, DatabaseFilePath);

                    result.RepairSuccessful = true;
                    result.RecoveryMethod = "SQLite recover command";
                }
            }
        }
        catch
        {
            // SQLite command line tool not available, try alternative methods;
            result.RecoveryMethod = "Manual recovery";

            // Create new database and import data from backup;
            await CreateAsync(Metadata, cancellationToken);

            // Try to read data from backup and import;
            try
            {
                using var backupConnection = new SqliteConnection($"Data Source={backupPath}");
                await backupConnection.OpenAsync(cancellationToken);

                // Try to read tables;
                var tables = new List<string>();
                using (var command = backupConnection.CreateCommand())
                {
                    command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
                    using var reader = await command.ExecuteReaderAsync(cancellationToken);

                    while (await reader.ReadAsync(cancellationToken))
                    {
                        tables.Add(reader.GetString(0));
                    }
                }

                result.TablesRecovered = tables.Count;

                // Note: Full data recovery would require more complex logic;
                // For now, we just note which tables exist;

                await backupConnection.CloseAsync();
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Failed to read backup: {ex.Message}");
            }
        }

        // 4. Re-open the database;
        await OpenAsync(cancellationToken);

        // 5. Verify integrity after repair;
        result.IntegrityAfterRepair = await VerifyIntegrityAsync(cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = result.RepairSuccessful || result.IntegrityAfterRepair.IsValid;

        _logger.Info($"Repair completed: {(result.Success ? "SUCCESS" : "FAILED")}");

        return result;
    }
    catch (Exception ex)
    {
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = false;
        result.Errors.Add($"Repair failed: {ex.Message}");

        _logger.Error($"Repair failed: {ex.Message}", ex);
        throw new KnowledgeBaseRepairException("Failed to repair knowledge base", ex);
    }
}

#endregion;

#region Public Methods - Security;

/// <summary>
/// Encrypts the knowledge base database.
/// </summary>
public async Task<EncryptionResult> EncryptAsync(
    string password,
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();

    if (string.IsNullOrEmpty(password))
        throw new ArgumentException("Password cannot be null or empty", nameof(password));

    var result = new EncryptionResult;
    {
        StartTime = DateTime.UtcNow,
        DatabaseFilePath = DatabaseFilePath;
    };

    try
    {
        _logger.Info("Encrypting knowledge base database");

        // 1. Close database if open;
        if (_isOpen)
        {
            await CloseAsync();
        }

        // 2. Create backup;
        var backupPath = DatabaseFilePath + ".pre_encrypt_backup";
        File.Copy(DatabaseFilePath, backupPath, true);
        result.BackupCreated = true;
        result.BackupPath = backupPath;

        // 3. Use SQLCipher or similar encryption;
        // Note: This requires SQLCipher extension or similar;

        // For now, we'll implement a basic file-level encryption;
        var encryptedBytes = await EncryptFileAsync(DatabaseFilePath, password, cancellationToken);

        // 4. Write encrypted data back;
        await File.WriteAllBytesAsync(DatabaseFilePath, encryptedBytes, cancellationToken);

        // 5. Update configuration with password;
        _configuration.Password = password;
        _configuration.IsEncrypted = true;

        // 6. Re-open with new password;
        await OpenAsync(cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = true;
        result.IsEncrypted = true;

        _logger.Info("Database encrypted successfully");

        return result;
    }
    catch (Exception ex)
    {
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = false;
        result.Error = ex.Message;

        _logger.Error($"Encryption failed: {ex.Message}", ex);
        throw new KnowledgeBaseSecurityException("Failed to encrypt database", ex);
    }
}

/// <summary>
/// Decrypts the knowledge base database.
/// </summary>
public async Task<DecryptionResult> DecryptAsync(
    string password,
    CancellationToken cancellationToken = default)
{
    ValidateNotDisposed();

    if (string.IsNullOrEmpty(password))
        throw new ArgumentException("Password cannot be null or empty", nameof(password));

    var result = new DecryptionResult;
    {
        StartTime = DateTime.UtcNow,
        DatabaseFilePath = DatabaseFilePath;
    };

    try
    {
        _logger.Info("Decrypting knowledge base database");

        // 1. Close database if open;
        if (_isOpen)
        {
            await CloseAsync();
        }

        // 2. Verify password;
        if (!await VerifyPasswordAsync(password, cancellationToken))
        {
            throw new KnowledgeBaseSecurityException("Invalid password");
        }

        // 3. Create backup;
        var backupPath = DatabaseFilePath + ".pre_decrypt_backup";
        File.Copy(DatabaseFilePath, backupPath, true);
        result.BackupCreated = true;
        result.BackupPath = backupPath;

        // 4. Decrypt the file;
        var decryptedBytes = await DecryptFileAsync(DatabaseFilePath, password, cancellationToken);

        // 5. Write decrypted data back;
        await File.WriteAllBytesAsync(DatabaseFilePath, decryptedBytes, cancellationToken);

        // 6. Update configuration;
        _configuration.Password = null;
        _configuration.IsEncrypted = false;

        // 7. Re-open without password;
        await OpenAsync(cancellationToken);

        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = true;
        result.IsDecrypted = true;

        _logger.Info("Database decrypted successfully");

        return result;
    }
    catch (Exception ex)
    {
        result.EndTime = DateTime.UtcNow;
        result.Duration = result.EndTime - result.StartTime;
        result.Success = false;
        result.Error = ex.Message;

        _logger.Error($"Decryption failed: {ex.Message}", ex);
        throw new KnowledgeBaseSecurityException("Failed to decrypt database", ex);
    }
}

private async Task<byte[]> EncryptFileAsync(string filePath, string password, CancellationToken cancellationToken)
{
    var fileBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);

    using var aes = Aes.Create();
    aes.KeySize = 256;
    aes.BlockSize = 128;
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    // Generate key from password;
    using var deriveBytes = new Rfc2898DeriveBytes(password, aes.BlockSize / 8, 10000, HashAlgorithmName.SHA256);
    aes.Key = deriveBytes.GetBytes(aes.KeySize / 8);
    aes.GenerateIV();

    using var encryptor = aes.CreateEncryptor();
    using var memoryStream = new MemoryStream();

    // Write IV first;
    await memoryStream.WriteAsync(aes.IV, 0, aes.IV.Length, cancellationToken);

    using var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write);
    await cryptoStream.WriteAsync(fileBytes, 0, fileBytes.Length, cancellationToken);
    cryptoStream.FlushFinalBlock();

    return memoryStream.ToArray();
}

private async Task<byte[]> DecryptFileAsync(string filePath, string password, CancellationToken cancellationToken)
{
    var encryptedBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);

    using var aes = Aes.Create();
    aes.KeySize = 256;
    aes.BlockSize = 128;
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.PKCS7;

    // Extract IV from beginning of file;
    var iv = new byte[aes.BlockSize / 8];
    Array.Copy(encryptedBytes, 0, iv, 0, iv.Length);
    aes.IV = iv;

    // Generate key from password;
    using var deriveBytes = new Rfc2898DeriveBytes(password, aes.BlockSize / 8, 10000, HashAlgorithmName.SHA256);
    aes.Key = deriveBytes.GetBytes(aes.KeySize / 8);

    using var decryptor = aes.CreateDecryptor();
    using var memoryStream = new MemoryStream();

    using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Write);
    await cryptoStream.WriteAsync(encryptedBytes, iv.Length, encryptedBytes.Length - iv.Length, cancellationToken);
    cryptoStream.FlushFinalBlock();

    return memoryStream.ToArray();
}

private async Task<bool> VerifyPasswordAsync(string password, CancellationToken cancellationToken)
{
    if (!_configuration.IsEncrypted)
        return true;

    try
    {
        // Try to open the database with the password;
        var testConnectionString = new SqliteConnectionStringBuilder;
        {
            DataSource = DatabaseFilePath,
            Password = password;
        }.ToString();

        using var testConnection = new SqliteConnection(testConnectionString);
        await testConnection.OpenAsync(cancellationToken);

        // Try a simple query to verify;
        using var command = testConnection.CreateCommand();
        command.CommandText = "SELECT 1";
        var result = await command.ExecuteScalarAsync(cancellationToken);

        await testConnection.CloseAsync();

        return result != null && Convert.ToInt32(result) == 1;
    }
    catch
    {
        return false;
    }
}

    #endregion;
}

/// <summary>
/// Knowledge base export data structure.
/// </summary>
[XmlRoot("KnowledgeBaseExport")]
public class KnowledgeBaseExport;
{
    [XmlElement("Metadata")]
    public KnowledgeBaseMetadata Metadata { get; set; }

    [XmlElement("ExportDate")]
    public DateTime ExportDate { get; set; }

    [XmlElement("ItemCount")]
    public int ItemCount { get; set; }

    [XmlArray("Items")]
    [XmlArrayItem("KnowledgeItem")]
    public List<KnowledgeItem> Items { get; set; } = new List<KnowledgeItem>();
}

/// <summary>
/// Batch operation for knowledge base.
/// </summary>
public class KnowledgeBaseOperation;
{
    public KnowledgeBaseOperationType Type { get; set; }
    public KnowledgeItem Item { get; set; }
    public string ItemId { get; set; }
    public bool Permanent { get; set; }
    public string CustomQuery { get; set; }
    public SqliteParameter[] Parameters { get; set; }
}

/// <summary>
/// Batch operation type.
/// </summary>
public enum KnowledgeBaseOperationType;
{
    Add,
    Update,
    Delete,
    Custom;
}

/// <summary>
/// Search criteria for advanced search.
/// </summary>
public class SearchCriteria;
{
    public string Title { get; set; }
    public string Description { get; set; }
    public string Category { get; set; }
    public List<string> Tags { get; set; }
    public double? MinPriority { get; set; }
    public double? MaxPriority { get; set; }
    public double? MinRelevance { get; set; }
    public double? MaxRelevance { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public KnowledgeItemStatus? Status { get; set; }
    public bool? IncludeArchived { get; set; }
    public string OrderBy { get; set; }
    public bool OrderDescending { get; set; }
    public int? Skip { get; set; }
    public int? Take { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();

        if (!string.IsNullOrEmpty(Title)) parts.Add($"Title: {Title}");
        if (!string.IsNullOrEmpty(Category)) parts.Add($"Category: {Category}");
        if (Tags != null && Tags.Any()) parts.Add($"Tags: {string.Join(",", Tags)}");
        if (FromDate.HasValue) parts.Add($"From: {FromDate.Value:yyyy-MM-dd}");
        if (ToDate.HasValue) parts.Add($"To: {ToDate.Value:yyyy-MM-dd}");

        return string.Join("; ", parts);
    }
}

/// <summary>
/// Maintenance options for knowledge base.
/// </summary>
public class MaintenanceOptions;
{
    public bool CheckIntegrity { get; set; } = true;
    public bool CreateBackup { get; set; } = true;
    public string BackupPath { get; set; }
    public CompressionLevel BackupCompressionLevel { get; set; } = CompressionLevel.Optimal;
    public bool IncludeIndexesInBackup { get; set; } = true;
    public bool CleanupExpiredItems { get; set; } = true;
    public bool ArchiveOldItems { get; set; } = false;
    public int? ArchiveThresholdDays { get; set; } = 365;
    public bool UpdateStatistics { get; set; } = true;
    public bool RebuildIndexes { get; set; } = false;
    public bool OptimizeDatabase { get; set; } = true;
    public bool CompactDatabase { get; set; } = false;
}

/// <summary>
/// Content analysis results.
/// </summary>
public class ContentAnalysis;
{
    public DateTime AnalysisDate { get; set; }
    public string DatabaseFilePath { get; set; }
    public int TotalItems { get; set; }
    public int AverageWordCount { get; set; }
    public int MinWordCount { get; set; }
    public int MaxWordCount { get; set; }
    public long TotalWordCount { get; set; }
    public Dictionary<string, int> ContentTypeDistribution { get; set; }
    public Dictionary<string, int> LanguageDistribution { get; set; }
    public List<DailyContentStat> DailyStats { get; set; }
    public double ItemsPerMB { get; set; }
    public TimeSpan ExecutionTime { get; set; }
}

/// <summary>
/// Daily content statistics.
/// </summary>
public class DailyContentStat;
{
    public DateTime Date { get; set; }
    public int ItemsCreated { get; set; }
    public double AveragePriority { get; set; }
    public double AverageRelevance { get; set; }
}

/// <summary>
/// Trend analysis results.
/// </summary>
public class TrendAnalysis;
{
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public DateTime AnalysisDate { get; set; }
    public List<DailyGrowth> DailyGrowth { get; set; }
    public double AverageDailyGrowth { get; set; }
    public DateTime? PeakGrowthDay { get; set; }
    public int PeakGrowthCount { get; set; }
    public double SevenDayMovingAverage { get; set; }
    public Dictionary<string, List<CategoryTrend>> CategoryTrends { get; set; }
    public Dictionary<string, TagTrend> TagTrends { get; set; }
    public TimeSpan ExecutionTime { get; set; }
}

/// <summary>
/// Daily growth statistics.
/// </summary>
public class DailyGrowth;
{
    public DateTime Date { get; set; }
    public int NewItems { get; set; }
    public int ArchivedItems { get; set; }
    public int UniqueCategories { get; set; }
    public int UniqueAuthors { get; set; }
}

/// <summary>
/// Category trend data.
/// </summary>
public class CategoryTrend;
{
    public DateTime Date { get; set; }
    public int ItemCount { get; set; }
}

/// <summary>
/// Tag trend data.
/// </summary>
public class TagTrend;
{
    public int TotalOccurrences { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }
    public double AverageFrequency { get; set; }
}

/// <summary>
/// Maintenance result.
/// </summary>
public class MaintenanceResult;
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string DatabaseFilePath { get; set; }
    public MaintenanceOptions Options { get; set; }
    public IntegrityCheckResult IntegrityCheck { get; set; }
    public BackupResult BackupResult { get; set; }
    public int ExpiredItemsCleaned { get; set; }
    public int ItemsArchived { get; set; }
    public IndexRebuildResult IndexRebuildResult { get; set; }
    public CompactionResult CompactionResult { get; set; }
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new List<string>();
}

/// <summary>
/// Repair result.
/// </summary>
public class RepairResult;
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string DatabaseFilePath { get; set; }
    public bool BackupCreated { get; set; }
    public string BackupPath { get; set; }
    public bool RepairSuccessful { get; set; }
    public string RecoveryMethod { get; set; }
    public int TablesRecovered { get; set; }
    public IntegrityCheckResult IntegrityAfterRepair { get; set; }
    public bool Success { get; set; }
    public List<string> Errors { get; set; } = new List<string>();
}

/// <summary>
/// Encryption result.
/// </summary>
public class EncryptionResult;
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string DatabaseFilePath { get; set; }
    public bool BackupCreated { get; set; }
    public string BackupPath { get; set; }
    public bool IsEncrypted { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; }
}

/// <summary>
/// Decryption result.
/// </summary>
public class DecryptionResult;
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string DatabaseFilePath { get; set; }
    public bool BackupCreated { get; set; }
    public string BackupPath { get; set; }
    public bool IsDecrypted { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; }
}

/// <summary>
/// Bulk update result.
/// </summary>
public class BulkUpdateResult;
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    public string FieldName { get; set; }
    public object FieldValue { get; set; }
    public int RowsAffected { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; }
}
