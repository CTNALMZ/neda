using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NEDA.Core.Common.Utilities;
using NEDA.Core.Logging;
using NEDA.KnowledgeBase.DataManagement.Configuration;
using NEDA.KnowledgeBase.DataManagement.Models;
using NEDA.KnowledgeBase.DataManagement.Interceptors;
using NEDA.KnowledgeBase.DataManagement.Exceptions;
using NEDA.KnowledgeBase.DataManagement.Entities;
using NEDA.KnowledgeBase.DataManagement.Entities.Conversations;
using NEDA.KnowledgeBase.DataManagement.Entities.Training;
using NEDA.KnowledgeBase.DataManagement.Entities.Knowledge;
using NEDA.KnowledgeBase.DataManagement.Entities.Analytics;

namespace NEDA.KnowledgeBase.DataManagement.DataAccess;
{
    /// <summary>
    /// Primary database context for NEDA knowledge base with multi-tenant support,
    /// audit logging, and advanced data management capabilities;
    /// </summary>
    public class DataContext : DbContext, IDataContext;
    {
        #region Private Fields;

        private readonly ILogger<DataContext> _logger;
        private readonly DatabaseConfig _config;
        private readonly IAuditLogger _auditLogger;
        private readonly ITenantProvider _tenantProvider;
        private readonly List<IChangeInterceptor> _interceptors;
        private readonly ISaveChangesInterceptor _saveChangesInterceptor;
        private readonly IDbConnectionManager _connectionManager;
        private readonly SemaphoreSlim _migrationSemaphore;
        private IDbContextTransaction _currentTransaction;
        private string _currentTenantId;
        private bool _isDisposed;
        private readonly object _transactionLock = new object();

        #endregion;

        #region Public Properties;

        /// <summary>
        /// Gets the current database connection state;
        /// </summary>
        public ConnectionState ConnectionState { get; private set; }

        /// <summary>
        /// Gets whether a transaction is currently active;
        /// </summary>
        public bool HasActiveTransaction => _currentTransaction != null;

        /// <summary>
        /// Gets the current tenant identifier;
        /// </summary>
        public string CurrentTenantId => _currentTenantId ?? "system";

        /// <summary>
        /// Gets the database configuration;
        /// </summary>
        public DatabaseConfig Configuration => _config;

        /// <summary>
        /// Gets the database statistics;
        /// </summary>
        public DatabaseStatistics Statistics { get; private set; }

        #endregion;

        #region DbSets - Knowledge Entities;

        /// <summary>
        /// Knowledge base articles and documentation;
        /// </summary>
        public DbSet<KnowledgeArticle> KnowledgeArticles { get; set; }

        /// <summary>
        /// Categorized knowledge topics;
        /// </summary>
        public DbSet<KnowledgeCategory> KnowledgeCategories { get; set; }

        /// <summary>
        /// Tags for knowledge organization;
        /// </summary>
        public DbSet<KnowledgeTag> KnowledgeTags { get; set; }

        /// <summary>
        /// Relationships between knowledge entities;
        /// </summary>
        public DbSet<KnowledgeRelationship> KnowledgeRelationships { get; set; }

        /// <summary>
        /// User contributions and edits;
        /// </summary>
        public DbSet<KnowledgeContribution> KnowledgeContributions { get; set; }

        /// <summary>
        /// Content revisions and history;
        /// </summary>
        public DbSet<ContentRevision> ContentRevisions { get; set; }

        #endregion;

        #region DbSets - Conversation Entities;

        /// <summary>
        /// Conversation sessions between users and AI;
        /// </summary>
        public DbSet<ConversationSession> ConversationSessions { get; set; }

        /// <summary>
        /// Individual messages within conversations;
        /// </summary>
        public DbSet<ConversationMessage> ConversationMessages { get; set; }

        /// <summary>
        /// Conversation contexts and metadata;
        /// </summary>
        public DbSet<ConversationContext> ConversationContexts { get; set; }

        /// <summary>
        /// Conversation intents and classifications;
        /// </summary>
        public DbSet<ConversationIntent> ConversationIntents { get; set; }

        /// <summary>
        /// User feedback on conversations;
        /// </summary>
        public DbSet<ConversationFeedback> ConversationFeedbacks { get; set; }

        #endregion;

        #region DbSets - Training Entities;

        /// <summary>
        /// AI training datasets;
        /// </summary>
        public DbSet<TrainingDataset> TrainingDatasets { get; set; }

        /// <summary>
        /// Training sessions and runs;
        /// </summary>
        public DbSet<TrainingSession> TrainingSessions { get; set; }

        /// <summary>
        /// Model versions and deployments;
        /// </summary>
        public DbSet<ModelVersion> ModelVersions { get; set; }

        /// <summary>
        /// Training metrics and results;
        /// </summary>
        public DbSet<TrainingMetric> TrainingMetrics { get; set; }

        /// <summary>
        /// Hyperparameter configurations;
        /// </summary>
        public DbSet<HyperparameterConfig> HyperparameterConfigs { get; set; }

        #endregion;

        #region DbSets - Analytics Entities;

        /// <summary>
        /// User interaction analytics;
        /// </summary>
        public DbSet<UserInteraction> UserInteractions { get; set; }

        /// <summary>
        /// System performance metrics;
        /// </summary>
        public DbSet<PerformanceMetric> PerformanceMetrics { get; set; }

        /// <summary>
        /// Error logs and diagnostics;
        /// </summary>
        public DbSet<ErrorLog> ErrorLogs { get; set; }

        /// <summary>
        /// Usage statistics and reports;
        /// </summary>
        public DbSet<UsageStatistic> UsageStatistics { get; set; }

        /// <summary>
        /// Audit trail records;
        /// </summary>
        public DbSet<AuditRecord> AuditRecords { get; set; }

        #endregion;

        #region DbSets - System Entities;

        /// <summary>
        /// System configuration settings;
        /// </summary>
        public DbSet<SystemSetting> SystemSettings { get; set; }

        /// <summary>
        /// User preferences and customizations;
        /// </summary>
        public DbSet<UserPreference> UserPreferences { get; set; }

        /// <summary>
        /// Scheduled tasks and jobs;
        /// </summary>
        public DbSet<ScheduledTask> ScheduledTasks { get; set; }

        /// <summary>
        /// Notification records;
        /// </summary>
        public DbSet<Notification> Notifications { get; set; }

        #endregion;

        #region Constructor;

        /// <summary>
        /// Initializes a new instance of the DataContext;
        /// </summary>
        /// <param name="options">DbContext options</param>
        /// <param name="logger">Logger instance</param>
        /// <param name="config">Database configuration</param>
        /// <param name="auditLogger">Audit logging service</param>
        /// <param name="tenantProvider">Tenant provider service</param>
        /// <param name="connectionManager">Connection manager service</param>
        /// <param name="interceptors">Change interceptors</param>
        public DataContext(
            DbContextOptions<DataContext> options,
            ILogger<DataContext> logger,
            IOptions<DatabaseConfig> config,
            IAuditLogger auditLogger,
            ITenantProvider tenantProvider,
            IDbConnectionManager connectionManager,
            IEnumerable<IChangeInterceptor> interceptors = null)
            : base(options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
            _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
            _tenantProvider = tenantProvider ?? throw new ArgumentNullException(nameof(tenantProvider));
            _connectionManager = connectionManager ?? throw new ArgumentNullException(nameof(connectionManager));

            _interceptors = interceptors?.ToList() ?? new List<IChangeInterceptor>();
            _migrationSemaphore = new SemaphoreSlim(1, 1);

            Statistics = new DatabaseStatistics();
            ConnectionState = ConnectionState.Closed;

            _logger.LogInformation("DataContext initialized with provider: {Provider}",
                Database.ProviderName);

            // Configure interceptors;
            ConfigureInterceptors();
        }

        #endregion;

        #region Configuration;

        /// <summary>
        /// Configures database connection and options;
        /// </summary>
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            if (!optionsBuilder.IsConfigured)
            {
                var connectionString = _connectionManager.GetConnectionString();

                switch (_config.Provider)
                {
                    case DatabaseProvider.SqlServer:
                        optionsBuilder.UseSqlServer(connectionString, sqlOptions =>
                        {
                            sqlOptions.EnableRetryOnFailure(
                                maxRetryCount: _config.MaxRetryCount,
                                maxRetryDelay: TimeSpan.FromSeconds(_config.MaxRetryDelay),
                                errorNumbersToAdd: null);
                            sqlOptions.CommandTimeout(_config.CommandTimeout);
                        });
                        break;

                    case DatabaseProvider.PostgreSQL:
                        optionsBuilder.UseNpgsql(connectionString, npgOptions =>
                        {
                            npgOptions.EnableRetryOnFailure(
                                maxRetryCount: _config.MaxRetryCount,
                                maxRetryDelay: TimeSpan.FromSeconds(_config.MaxRetryDelay));
                            npgOptions.CommandTimeout(_config.CommandTimeout);
                        });
                        break;

                    case DatabaseProvider.SQLite:
                        optionsBuilder.UseSqlite(connectionString, sqliteOptions =>
                        {
                            sqliteOptions.CommandTimeout(_config.CommandTimeout);
                        });
                        break;

                    case DatabaseProvider.MySQL:
                        optionsBuilder.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString), mysqlOptions =>
                        {
                            mysqlOptions.EnableRetryOnFailure(
                                maxRetryCount: _config.MaxRetryCount,
                                maxRetryDelay: TimeSpan.FromSeconds(_config.MaxRetryDelay));
                            mysqlOptions.CommandTimeout(_config.CommandTimeout);
                        });
                        break;

                    default:
                        throw new DatabaseConfigurationException($"Unsupported database provider: {_config.Provider}");
                }

                // Enable sensitive data logging only in development;
                if (_config.EnableSensitiveDataLogging)
                {
                    optionsBuilder.EnableSensitiveDataLogging();
                }

                // Enable detailed errors;
                if (_config.EnableDetailedErrors)
                {
                    optionsBuilder.EnableDetailedErrors();
                }

                // Configure logging;
                optionsBuilder.LogTo(
                    message => _logger.LogDebug("EF Core: {Message}", message),
                    new[] { DbLoggerCategory.Database.Command.Name },
                    _config.LogLevel,
                    _config.DbContextLoggerOptions);
            }

            base.OnConfiguring(optionsBuilder);
        }

        /// <summary>
        /// Configures entity relationships and constraints;
        /// </summary>
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            if (modelBuilder == null)
                throw new ArgumentNullException(nameof(modelBuilder));

            try
            {
                _logger.LogInformation("Building database model for provider: {Provider}", Database.ProviderName);

                // Apply global filters for multi-tenancy;
                if (_config.EnableMultiTenancy)
                {
                    ApplyTenantFilters(modelBuilder);
                }

                // Apply soft delete filters;
                if (_config.EnableSoftDelete)
                {
                    ApplySoftDeleteFilters(modelBuilder);
                }

                // Configure entity relationships;
                ConfigureKnowledgeEntities(modelBuilder);
                ConfigureConversationEntities(modelBuilder);
                ConfigureTrainingEntities(modelBuilder);
                ConfigureAnalyticsEntities(modelBuilder);
                ConfigureSystemEntities(modelBuilder);

                // Configure indices for performance;
                ConfigureIndices(modelBuilder);

                // Configure value converters and comparers;
                ConfigureValueConverters(modelBuilder);

                // Seed initial data if configured;
                if (_config.SeedInitialData)
                {
                    SeedData(modelBuilder);
                }

                _logger.LogInformation("Database model built successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building database model");
                throw new DatabaseConfigurationException("Failed to build database model", ex);
            }
        }

        /// <summary>
        /// Configures interceptors for change tracking;
        /// </summary>
        private void ConfigureInterceptors()
        {
            // Add built-in interceptors;
            _interceptors.Add(new AuditInterceptor(_auditLogger));
            _interceptors.Add(new SoftDeleteInterceptor());
            _interceptors.Add(new TenantInterceptor(_tenantProvider));

            if (_config.EnableConcurrencyControl)
            {
                _interceptors.Add(new ConcurrencyInterceptor());
            }

            _logger.LogDebug("Configured {Count} change interceptors", _interceptors.Count);
        }

        /// <summary>
        /// Applies tenant filters to all tenant-aware entities;
        /// </summary>
        private void ApplyTenantFilters(ModelBuilder modelBuilder)
        {
            var tenantAwareEntities = modelBuilder.Model.GetEntityTypes()
                .Where(e => typeof(ITenantAware).IsAssignableFrom(e.ClrType));

            foreach (var entityType in tenantAwareEntities)
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var tenantIdProperty = Expression.Property(parameter, nameof(ITenantAware.TenantId));
                var currentTenantId = Expression.Constant(CurrentTenantId);
                var equalsExpression = Expression.Equal(tenantIdProperty, currentTenantId);

                var lambda = Expression.Lambda(equalsExpression, parameter);
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
            }
        }

        /// <summary>
        /// Applies soft delete filters to all soft-deletable entities;
        /// </summary>
        private void ApplySoftDeleteFilters(ModelBuilder modelBuilder)
        {
            var softDeleteEntities = modelBuilder.Model.GetEntityTypes()
                .Where(e => typeof(ISoftDeletable).IsAssignableFrom(e.ClrType));

            foreach (var entityType in softDeleteEntities)
            {
                var parameter = Expression.Parameter(entityType.ClrType, "e");
                var isDeletedProperty = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                var falseConstant = Expression.Constant(false);
                var equalsExpression = Expression.Equal(isDeletedProperty, falseConstant);

                var lambda = Expression.Lambda(equalsExpression, parameter);
                modelBuilder.Entity(entityType.ClrType).HasQueryFilter(lambda);
            }
        }

        /// <summary>
        /// Configures knowledge entity relationships;
        /// </summary>
        private void ConfigureKnowledgeEntities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<KnowledgeArticle>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Slug).IsUnique();
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.UpdatedAt);

                entity.Property(e => e.Title)
                    .IsRequired()
                    .HasMaxLength(500);

                entity.Property(e => e.Content)
                    .IsRequired()
                    .HasColumnType("ntext");

                entity.HasOne(e => e.Category)
                    .WithMany(c => c.Articles)
                    .HasForeignKey(e => e.CategoryId)
                    .OnDelete(DeleteBehavior.Restrict);

                entity.HasMany(e => e.Tags)
                    .WithMany(t => t.Articles)
                    .UsingEntity<Dictionary<string, object>>(
                        "ArticleTags",
                        j => j.HasOne<KnowledgeTag>().WithMany().HasForeignKey("TagId"),
                        j => j.HasOne<KnowledgeArticle>().WithMany().HasForeignKey("ArticleId"));

                entity.HasMany(e => e.Revisions)
                    .WithOne(r => r.Article)
                    .HasForeignKey(r => r.ArticleId);

                entity.HasMany(e => e.Contributions)
                    .WithOne(c => c.Article)
                    .HasForeignKey(c => c.ArticleId);
            });

            modelBuilder.Entity<KnowledgeCategory>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.HasIndex(e => e.ParentId);

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.HasOne(e => e.Parent)
                    .WithMany(p => p.Children)
                    .HasForeignKey(e => e.ParentId)
                    .OnDelete(DeleteBehavior.Restrict);
            });

            modelBuilder.Entity<KnowledgeTag>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name).IsUnique();

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(100);
            });
        }

        /// <summary>
        /// Configures conversation entity relationships;
        /// </summary>
        private void ConfigureConversationEntities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConversationSession>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.LastActivityAt);

                entity.Property(e => e.SessionId)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.HasMany(e => e.Messages)
                    .WithOne(m => m.Session)
                    .HasForeignKey(m => m.SessionId)
                    .OnDelete(DeleteBehavior.Cascade);

                entity.HasOne(e => e.Context)
                    .WithOne(c => c.Session)
                    .HasForeignKey<ConversationContext>(c => c.SessionId);
            });

            modelBuilder.Entity<ConversationMessage>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.SessionId);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.SenderType);

                entity.Property(e => e.Content)
                    .IsRequired()
                    .HasMaxLength(4000);

                entity.Property(e => e.Metadata)
                    .HasColumnType("jsonb");
            });
        }

        /// <summary>
        /// Configures training entity relationships;
        /// </summary>
        private void ConfigureTrainingEntities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TrainingDataset>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name).IsUnique();
                entity.HasIndex(e => e.CreatedAt);

                entity.Property(e => e.Name)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.Property(e => e.DataPath)
                    .IsRequired()
                    .HasMaxLength(500);

                entity.HasMany(e => e.TrainingSessions)
                    .WithOne(t => t.Dataset)
                    .HasForeignKey(t => t.DatasetId);
            });

            modelBuilder.Entity<TrainingSession>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.StartedAt);
                entity.HasIndex(e => e.Status);

                entity.Property(e => e.Configuration)
                    .HasColumnType("jsonb");

                entity.HasMany(e => e.Metrics)
                    .WithOne(m => m.Session)
                    .HasForeignKey(m => m.SessionId);

                entity.HasOne(e => e.ModelVersion)
                    .WithOne(m => m.TrainingSession)
                    .HasForeignKey<ModelVersion>(m => m.TrainingSessionId);
            });
        }

        /// <summary>
        /// Configures analytics entity relationships;
        /// </summary>
        private void ConfigureAnalyticsEntities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<UserInteraction>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.UserId);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.ActionType);

                entity.Property(e => e.ActionType)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.Details)
                    .HasColumnType("jsonb");
            });

            modelBuilder.Entity<AuditRecord>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Timestamp);
                entity.HasIndex(e => e.EntityType);
                entity.HasIndex(e => e.OperationType);

                entity.Property(e => e.EntityType)
                    .IsRequired()
                    .HasMaxLength(100);

                entity.Property(e => e.Changes)
                    .HasColumnType("jsonb");
            });
        }

        /// <summary>
        /// Configures system entity relationships;
        /// </summary>
        private void ConfigureSystemEntities(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<SystemSetting>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Key).IsUnique();

                entity.Property(e => e.Key)
                    .IsRequired()
                    .HasMaxLength(200);

                entity.Property(e => e.Value)
                    .IsRequired()
                    .HasMaxLength(4000);
            });
        }

        /// <summary>
        /// Configures database indices for performance;
        /// </summary>
        private void ConfigureIndices(ModelBuilder modelBuilder)
        {
            // Common index patterns for frequently queried fields;
            var entityTypes = modelBuilder.Model.GetEntityTypes();

            foreach (var entityType in entityTypes)
            {
                var properties = entityType.GetProperties();

                // Index on CreatedAt for temporal queries;
                var createdAtProperty = properties.FirstOrDefault(p => p.Name == "CreatedAt");
                if (createdAtProperty != null)
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .HasIndex("CreatedAt")
                        .HasDatabaseName($"IX_{entityType.GetTableName()}_CreatedAt");
                }

                // Index on UpdatedAt;
                var updatedAtProperty = properties.FirstOrDefault(p => p.Name == "UpdatedAt");
                if (updatedAtProperty != null)
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .HasIndex("UpdatedAt")
                        .HasDatabaseName($"IX_{entityType.GetTableName()}_UpdatedAt");
                }

                // Composite indices for common query patterns;
                if (typeof(IConversationEntity).IsAssignableFrom(entityType.ClrType))
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .HasIndex("SessionId", "Timestamp")
                        .HasDatabaseName($"IX_{entityType.GetTableName()}_SessionTimestamp");
                }

                if (typeof(IAnalyticsEntity).IsAssignableFrom(entityType.ClrType))
                {
                    modelBuilder.Entity(entityType.ClrType)
                        .HasIndex("Timestamp", "UserId")
                        .HasDatabaseName($"IX_{entityType.GetTableName()}_TimestampUser");
                }
            }
        }

        /// <summary>
        /// Configures value converters for complex types;
        /// </summary>
        private void ConfigureValueConverters(ModelBuilder modelBuilder)
        {
            // JSON converters for complex properties;
            var jsonConverter = new JsonValueConverter<string>();

            // Configure JSON properties;
            var entitiesWithJsonProperties = modelBuilder.Model.GetEntityTypes()
                .SelectMany(e => e.GetProperties())
                .Where(p => p.Name.EndsWith("Json") || p.Name == "Metadata" || p.Name == "Configuration");

            foreach (var property in entitiesWithJsonProperties)
            {
                property.SetValueConverter(jsonConverter);
            }

            // DateTime converters for UTC storage;
            var dateTimeConverter = new DateTimeToUtcConverter();
            var dateTimeProperties = modelBuilder.Model.GetEntityTypes()
                .SelectMany(e => e.GetProperties())
                .Where(p => p.ClrType == typeof(DateTime) || p.ClrType == typeof(DateTime?));

            foreach (var property in dateTimeProperties)
            {
                property.SetValueConverter(dateTimeConverter);
            }
        }

        /// <summary>
        /// Seeds initial data into the database;
        /// </summary>
        private void SeedData(ModelBuilder modelBuilder)
        {
            _logger.LogInformation("Seeding initial database data...");

            // Seed system settings;
            modelBuilder.Entity<SystemSetting>().HasData(
                new SystemSetting;
                {
                    Id = Guid.NewGuid(),
                    Key = "System.Version",
                    Value = "1.0.0",
                    Description = "Current system version",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                },
                new SystemSetting;
                {
                    Id = Guid.NewGuid(),
                    Key = "System.MaintenanceMode",
                    Value = "false",
                    Description = "System maintenance mode flag",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                }
            );

            // Seed default knowledge categories;
            modelBuilder.Entity<KnowledgeCategory>().HasData(
                new KnowledgeCategory;
                {
                    Id = Guid.NewGuid(),
                    Name = "General",
                    Description = "General knowledge articles",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                },
                new KnowledgeCategory;
                {
                    Id = Guid.NewGuid(),
                    Name = "Technical",
                    Description = "Technical documentation",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow;
                }
            );

            _logger.LogInformation("Initial data seeded successfully");
        }

        #endregion;

        #region Connection Management;

        /// <summary>
        /// Opens a connection to the database;
        /// </summary>
        public async Task OpenConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (ConnectionState == ConnectionState.Open)
            {
                _logger.LogDebug("Database connection is already open");
                return;
            }

            try
            {
                _logger.LogInformation("Opening database connection...");

                await Database.OpenConnectionAsync(cancellationToken);

                ConnectionState = ConnectionState.Open;
                Statistics.ConnectionOpened();

                _logger.LogInformation("Database connection opened successfully");
            }
            catch (Exception ex)
            {
                ConnectionState = ConnectionState.Broken;
                _logger.LogError(ex, "Failed to open database connection");
                throw new DatabaseConnectionException("Failed to open database connection", ex);
            }
        }

        /// <summary>
        /// Closes the database connection;
        /// </summary>
        public async Task CloseConnectionAsync()
        {
            if (ConnectionState == ConnectionState.Closed)
            {
                _logger.LogDebug("Database connection is already closed");
                return;
            }

            try
            {
                _logger.LogInformation("Closing database connection...");

                await Database.CloseConnectionAsync();

                ConnectionState = ConnectionState.Closed;
                Statistics.ConnectionClosed();

                _logger.LogInformation("Database connection closed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error closing database connection");
                throw new DatabaseConnectionException("Failed to close database connection", ex);
            }
        }

        /// <summary>
        /// Tests the database connection;
        /// </summary>
        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogDebug("Testing database connection...");

                var canConnect = await Database.CanConnectAsync(cancellationToken);

                if (canConnect)
                {
                    _logger.LogDebug("Database connection test passed");
                    return true;
                }
                else;
                {
                    _logger.LogWarning("Database connection test failed");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database connection test failed with exception");
                return false;
            }
        }

        /// <summary>
        /// Migrates the database to the latest version;
        /// </summary>
        public async Task MigrateAsync(CancellationToken cancellationToken = default)
        {
            await _migrationSemaphore.WaitAsync(cancellationToken);

            try
            {
                _logger.LogInformation("Starting database migration...");

                if (!await Database.CanConnectAsync(cancellationToken))
                {
                    throw new DatabaseMigrationException("Cannot connect to database for migration");
                }

                var pendingMigrations = await Database.GetPendingMigrationsAsync(cancellationToken);
                var pendingCount = pendingMigrations.Count();

                if (pendingCount == 0)
                {
                    _logger.LogInformation("Database is already up to date");
                    return;
                }

                _logger.LogInformation("Applying {Count} pending migration(s)...", pendingCount);

                foreach (var migration in pendingMigrations)
                {
                    _logger.LogDebug("Applying migration: {Migration}", migration);
                }

                await Database.MigrateAsync(cancellationToken);

                var appliedMigrations = await Database.GetAppliedMigrationsAsync(cancellationToken);
                _logger.LogInformation("Database migration completed successfully. Applied migrations: {Count}",
                    appliedMigrations.Count());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database migration failed");
                throw new DatabaseMigrationException("Failed to migrate database", ex);
            }
            finally
            {
                _migrationSemaphore.Release();
            }
        }

        #endregion;

        #region Transaction Management;

        /// <summary>
        /// Begins a new database transaction;
        /// </summary>
        /// <param name="isolationLevel">Transaction isolation level</param>
        public async Task<IDbContextTransaction> BeginTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted)
        {
            lock (_transactionLock)
            {
                if (_currentTransaction != null)
                {
                    throw new InvalidOperationException("A transaction is already in progress");
                }
            }

            try
            {
                _logger.LogDebug("Beginning database transaction with isolation level: {IsolationLevel}",
                    isolationLevel);

                _currentTransaction = await Database.BeginTransactionAsync(isolationLevel);

                Statistics.TransactionStarted();

                _logger.LogDebug("Database transaction started successfully");

                return _currentTransaction;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to begin database transaction");
                throw new DatabaseTransactionException("Failed to begin transaction", ex);
            }
        }

        /// <summary>
        /// Commits the current transaction;
        /// </summary>
        public async Task CommitTransactionAsync()
        {
            if (_currentTransaction == null)
            {
                throw new InvalidOperationException("No transaction to commit");
            }

            try
            {
                _logger.LogDebug("Committing database transaction...");

                await _currentTransaction.CommitAsync();

                Statistics.TransactionCommitted();

                _logger.LogDebug("Database transaction committed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to commit database transaction");
                await RollbackTransactionAsync();
                throw new DatabaseTransactionException("Failed to commit transaction", ex);
            }
            finally
            {
                DisposeTransaction();
            }
        }

        /// <summary>
        /// Rolls back the current transaction;
        /// </summary>
        public async Task RollbackTransactionAsync()
        {
            if (_currentTransaction == null)
            {
                throw new InvalidOperationException("No transaction to rollback");
            }

            try
            {
                _logger.LogDebug("Rolling back database transaction...");

                await _currentTransaction.RollbackAsync();

                Statistics.TransactionRolledBack();

                _logger.LogDebug("Database transaction rolled back successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rollback database transaction");
                throw new DatabaseTransactionException("Failed to rollback transaction", ex);
            }
            finally
            {
                DisposeTransaction();
            }
        }

        /// <summary>
        /// Disposes the current transaction;
        /// </summary>
        private void DisposeTransaction()
        {
            if (_currentTransaction != null)
            {
                _currentTransaction.Dispose();
                _currentTransaction = null;
            }
        }

        /// <summary>
        /// Executes a function within a transaction;
        /// </summary>
        public async Task<T> ExecuteInTransactionAsync<T>(
            Func<Task<T>> operation,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted)
        {
            await using var transaction = await BeginTransactionAsync(isolationLevel);

            try
            {
                var result = await operation();
                await CommitTransactionAsync();
                return result;
            }
            catch
            {
                await RollbackTransactionAsync();
                throw;
            }
        }

        #endregion;

        #region Save Changes;

        /// <summary>
        /// Saves all changes to the database with interceptors;
        /// </summary>
        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return await SaveChangesAsync(true, cancellationToken);
        }

        /// <summary>
        /// Saves changes with optional accept changes;
        /// </summary>
        public async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
        {
            var changesDetected = ChangeTracker.HasChanges();

            if (!changesDetected)
            {
                _logger.LogDebug("No changes detected to save");
                return 0;
            }

            try
            {
                // Execute pre-save interceptors;
                await ExecutePreSaveInterceptorsAsync(cancellationToken);

                // Apply tenant ID to new entities;
                if (_config.EnableMultiTenancy)
                {
                    ApplyTenantToNewEntities();
                }

                // Set audit fields;
                SetAuditFields();

                // Validate entities before saving;
                if (_config.EnableEntityValidation)
                {
                    ValidateEntities();
                }

                _logger.LogDebug("Saving {Count} changes to database",
                    ChangeTracker.Entries().Count(e => e.State != EntityState.Unchanged));

                var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

                Statistics.ChangesSaved(result);

                _logger.LogDebug("Successfully saved {Count} changes", result);

                // Execute post-save interceptors;
                await ExecutePostSaveInterceptorsAsync(cancellationToken);

                return result;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                _logger.LogError(ex, "Concurrency conflict during save changes");
                await HandleConcurrencyExceptionAsync(ex);
                throw new DatabaseConcurrencyException("Concurrency conflict occurred", ex);
            }
            catch (DbUpdateException ex)
            {
                _logger.LogError(ex, "Database update error during save changes");
                throw new DatabaseUpdateException("Failed to save changes to database", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during save changes");
                throw new DatabaseOperationException("Failed to save changes", ex);
            }
        }

        /// <summary>
        /// Executes pre-save interceptors;
        /// </summary>
        private async Task ExecutePreSaveInterceptorsAsync(CancellationToken cancellationToken)
        {
            foreach (var interceptor in _interceptors)
            {
                try
                {
                    await interceptor.BeforeSaveChangesAsync(this, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pre-save interceptor {Interceptor} failed",
                        interceptor.GetType().Name);

                    if (!_config.ContinueOnInterceptorError)
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Executes post-save interceptors;
        /// </summary>
        private async Task ExecutePostSaveInterceptorsAsync(CancellationToken cancellationToken)
        {
            foreach (var interceptor in _interceptors)
            {
                try
                {
                    await interceptor.AfterSaveChangesAsync(this, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-save interceptor {Interceptor} failed",
                        interceptor.GetType().Name);

                    if (!_config.ContinueOnInterceptorError)
                    {
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Applies tenant ID to new entities;
        /// </summary>
        private void ApplyTenantToNewEntities()
        {
            var newEntities = ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added && e.Entity is ITenantAware)
                .Select(e => e.Entity as ITenantAware);

            foreach (var entity in newEntities)
            {
                if (string.IsNullOrEmpty(entity.TenantId))
                {
                    entity.TenantId = CurrentTenantId;
                }
            }
        }

        /// <summary>
        /// Sets audit fields (CreatedAt, UpdatedAt) on entities;
        /// </summary>
        private void SetAuditFields()
        {
            var now = DateTime.UtcNow;
            var entries = ChangeTracker.Entries()
                .Where(e => e.Entity is IAuditable &&
                    (e.State == EntityState.Added || e.State == EntityState.Modified));

            foreach (var entry in entries)
            {
                var entity = (IAuditable)entry.Entity;

                if (entry.State == EntityState.Added)
                {
                    entity.CreatedAt = now;
                    entity.CreatedBy = _tenantProvider?.GetCurrentUserId() ?? "system";
                }

                entity.UpdatedAt = now;
                entity.UpdatedBy = _tenantProvider?.GetCurrentUserId() ?? "system";
            }
        }

        /// <summary>
        /// Validates entities before saving;
        /// </summary>
        private void ValidateEntities()
        {
            var errors = new List<string>();
            var entries = ChangeTracker.Entries()
                .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

            foreach (var entry in entries)
            {
                var validationContext = new ValidationContext(entry.Entity);
                var validationResults = new List<ValidationResult>();

                if (!Validator.TryValidateObject(entry.Entity, validationContext, validationResults, true))
                {
                    errors.AddRange(validationResults.Select(r =>
                        $"{entry.Entity.GetType().Name}: {r.ErrorMessage}"));
                }
            }

            if (errors.Any())
            {
                throw new DatabaseValidationException("Entity validation failed", errors);
            }
        }

        /// <summary>
        /// Handles concurrency exceptions;
        /// </summary>
        private async Task HandleConcurrencyExceptionAsync(DbUpdateConcurrencyException ex)
        {
            foreach (var entry in ex.Entries)
            {
                try
                {
                    // Try to refresh the entity from database;
                    await entry.ReloadAsync();

                    // Mark as modified to retry
                    entry.State = EntityState.Modified;
                }
                catch (Exception reloadEx)
                {
                    _logger.LogError(reloadEx, "Failed to reload entity after concurrency conflict");
                    entry.State = EntityState.Detached;
                }
            }
        }

        #endregion;

        #region Query Operations;

        /// <summary>
        /// Executes a raw SQL query and returns results;
        /// </summary>
        public async Task<IEnumerable<T>> QueryAsync<T>(string sql, params object[] parameters)
            where T : class;
        {
            try
            {
                _logger.LogDebug("Executing raw SQL query: {Sql}", sql);

                var result = await Set<T>().FromSqlRaw(sql, parameters).ToListAsync();

                Statistics.QueryExecuted();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing raw SQL query");
                throw new DatabaseQueryException("Failed to execute raw SQL query", ex);
            }
        }

        /// <summary>
        /// Executes a stored procedure;
        /// </summary>
        public async Task<int> ExecuteStoredProcedureAsync(string procedureName, params object[] parameters)
        {
            try
            {
                _logger.LogDebug("Executing stored procedure: {Procedure}", procedureName);

                var paramNames = parameters.Select((p, i) => $"@p{i}").ToArray();
                var paramList = string.Join(", ", paramNames);
                var sql = $"EXEC {procedureName} {paramList}";

                var result = await Database.ExecuteSqlRawAsync(sql, parameters);

                Statistics.StoredProcedureExecuted();

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing stored procedure");
                throw new DatabaseQueryException("Failed to execute stored procedure", ex);
            }
        }

        /// <summary>
        /// Creates a compiled query for better performance;
        /// </summary>
        public Func<DataContext, IAsyncEnumerable<T>> CompileQuery<T>(
            Expression<Func<DataContext, IQueryable<T>>> queryExpression)
        {
            return EF.CompileAsyncQuery(queryExpression);
        }

        #endregion;

        #region Bulk Operations;

        /// <summary>
        /// Performs bulk insert operation;
        /// </summary>
        public async Task BulkInsertAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default)
            where T : class;
        {
            if (entities == null)
                throw new ArgumentNullException(nameof(entities));

            try
            {
                _logger.LogDebug("Starting bulk insert of {Count} entities", entities.Count());

                await Set<T>().AddRangeAsync(entities, cancellationToken);
                await SaveChangesAsync(cancellationToken);

                Statistics.BulkInsertPerformed(entities.Count());

                _logger.LogDebug("Bulk insert completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bulk insert");
                throw new DatabaseOperationException("Failed to perform bulk insert", ex);
            }
        }

        /// <summary>
        /// Performs bulk update operation;
        /// </summary>
        public async Task BulkUpdateAsync<T>(
            IEnumerable<T> entities,
            Action<T> updateAction,
            CancellationToken cancellationToken = default)
            where T : class;
        {
            if (entities == null)
                throw new ArgumentNullException(nameof(entities));

            if (updateAction == null)
                throw new ArgumentNullException(nameof(updateAction));

            try
            {
                _logger.LogDebug("Starting bulk update of {Count} entities", entities.Count());

                foreach (var entity in entities)
                {
                    updateAction(entity);
                    Entry(entity).State = EntityState.Modified;
                }

                await SaveChangesAsync(cancellationToken);

                Statistics.BulkUpdatePerformed(entities.Count());

                _logger.LogDebug("Bulk update completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bulk update");
                throw new DatabaseOperationException("Failed to perform bulk update", ex);
            }
        }

        /// <summary>
        /// Performs bulk delete operation;
        /// </summary>
        public async Task BulkDeleteAsync<T>(
            Expression<Func<T, bool>> predicate,
            CancellationToken cancellationToken = default)
            where T : class;
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            try
            {
                _logger.LogDebug("Starting bulk delete operation");

                var entities = await Set<T>().Where(predicate).ToListAsync(cancellationToken);

                if (entities.Any())
                {
                    Set<T>().RemoveRange(entities);
                    await SaveChangesAsync(cancellationToken);

                    Statistics.BulkDeletePerformed(entities.Count);

                    _logger.LogDebug("Bulk delete completed: {Count} entities deleted", entities.Count);
                }
                else;
                {
                    _logger.LogDebug("No entities found to delete");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during bulk delete");
                throw new DatabaseOperationException("Failed to perform bulk delete", ex);
            }
        }

        #endregion;

        #region Diagnostics and Maintenance;

        /// <summary>
        /// Gets database diagnostics and health information;
        /// </summary>
        public async Task<DatabaseDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                await OpenConnectionAsync(cancellationToken);

                var diagnostics = new DatabaseDiagnostics;
                {
                    ConnectionState = ConnectionState,
                    ProviderName = Database.ProviderName,
                    DatabaseName = Database.GetDbConnection().Database,
                    ServerVersion = Database.GetDbConnection().ServerVersion,
                    Statistics = Statistics.Clone(),
                    IsHealthy = await TestConnectionAsync(cancellationToken),
                    MigrationStatus = await GetMigrationStatusAsync(cancellationToken),
                    TableStatistics = await GetTableStatisticsAsync(cancellationToken),
                    Timestamp = DateTime.UtcNow;
                };

                return diagnostics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting database diagnostics");
                throw new DatabaseDiagnosticsException("Failed to get database diagnostics", ex);
            }
        }

        /// <summary>
        /// Gets migration status information;
        /// </summary>
        private async Task<MigrationStatus> GetMigrationStatusAsync(CancellationToken cancellationToken)
        {
            var appliedMigrations = await Database.GetAppliedMigrationsAsync(cancellationToken);
            var pendingMigrations = await Database.GetPendingMigrationsAsync(cancellationToken);
            var allMigrations = await Database.GetMigrationsAsync(cancellationToken);

            return new MigrationStatus;
            {
                AppliedCount = appliedMigrations.Count(),
                PendingCount = pendingMigrations.Count(),
                TotalCount = allMigrations.Count(),
                IsUpToDate = !pendingMigrations.Any(),
                LastAppliedMigration = appliedMigrations.LastOrDefault(),
                FirstPendingMigration = pendingMigrations.FirstOrDefault()
            };
        }

        /// <summary>
        /// Gets table statistics;
        /// </summary>
        private async Task<List<TableStatistic>> GetTableStatisticsAsync(CancellationToken cancellationToken)
        {
            var statistics = new List<TableStatistic>();
            var entityTypes = Model.GetEntityTypes();

            foreach (var entityType in entityTypes)
            {
                try
                {
                    var tableName = entityType.GetTableName();
                    var count = await Set(entityType.ClrType).CountAsync(cancellationToken);

                    statistics.Add(new TableStatistic;
                    {
                        TableName = tableName,
                        EntityType = entityType.ClrType.Name,
                        RowCount = count,
                        LastAnalyzed = DateTime.UtcNow;
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to get statistics for table {Table}",
                        entityType.GetTableName());
                }
            }

            return statistics;
        }

        /// <summary>
        /// Clears the change tracker to free memory;
        /// </summary>
        public void ClearChangeTracker()
        {
            ChangeTracker.Clear();
            _logger.LogDebug("Change tracker cleared");
        }

        /// <summary>
        /// Executes database maintenance tasks;
        /// </summary>
        public async Task ExecuteMaintenanceAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Starting database maintenance...");

                // Rebuild indexes if supported;
                if (_config.EnableIndexMaintenance)
                {
                    await RebuildIndexesAsync(cancellationToken);
                }

                // Update statistics;
                if (_config.EnableStatisticsUpdate)
                {
                    await UpdateStatisticsAsync(cancellationToken);
                }

                // Clean up old data;
                if (_config.EnableDataCleanup)
                {
                    await CleanupOldDataAsync(cancellationToken);
                }

                _logger.LogInformation("Database maintenance completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during database maintenance");
                throw new DatabaseMaintenanceException("Failed to execute maintenance tasks", ex);
            }
        }

        /// <summary>
        /// Rebuilds database indexes;
        /// </summary>
        private async Task RebuildIndexesAsync(CancellationToken cancellationToken)
        {
            // Implementation depends on database provider;
            switch (_config.Provider)
            {
                case DatabaseProvider.SqlServer:
                    await Database.ExecuteSqlRawAsync("EXEC sp_msforeachtable 'ALTER INDEX ALL ON ? REBUILD'",
                        cancellationToken);
                    break;

                case DatabaseProvider.PostgreSQL:
                    await Database.ExecuteSqlRawAsync("REINDEX DATABASE " + Database.GetDbConnection().Database,
                        cancellationToken);
                    break;

                default:
                    _logger.LogWarning("Index rebuilding not supported for provider: {Provider}", _config.Provider);
                    break;
            }
        }

        /// <summary>
        /// Updates database statistics;
        /// </summary>
        private async Task UpdateStatisticsAsync(CancellationToken cancellationToken)
        {
            switch (_config.Provider)
            {
                case DatabaseProvider.SqlServer:
                    await Database.ExecuteSqlRawAsync("EXEC sp_updatestats", cancellationToken);
                    break;

                case DatabaseProvider.PostgreSQL:
                    await Database.ExecuteSqlRawAsync("ANALYZE", cancellationToken);
                    break;

                default:
                    _logger.LogWarning("Statistics update not supported for provider: {Provider}", _config.Provider);
                    break;
            }
        }

        /// <summary>
        /// Cleans up old data based on retention policy;
        /// </summary>
        private async Task CleanupOldDataAsync(CancellationToken cancellationToken)
        {
            var retentionDate = DateTime.UtcNow.AddDays(-_config.DataRetentionDays);

            // Clean old audit records;
            var oldAuditRecords = await AuditRecords;
                .Where(a => a.Timestamp < retentionDate)
                .ToListAsync(cancellationToken);

            if (oldAuditRecords.Any())
            {
                AuditRecords.RemoveRange(oldAuditRecords);
                await SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleaned up {Count} old audit records", oldAuditRecords.Count);
            }

            // Clean old error logs;
            var oldErrorLogs = await ErrorLogs;
                .Where(e => e.Timestamp < retentionDate)
                .ToListAsync(cancellationToken);

            if (oldErrorLogs.Any())
            {
                ErrorLogs.RemoveRange(oldErrorLogs);
                await SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Cleaned up {Count} old error logs", oldErrorLogs.Count);
            }
        }

        #endregion;

        #region IDisposable Implementation;

        /// <summary>
        /// Disposes the database context and releases all resources;
        /// </summary>
        public override void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected dispose method;
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (_isDisposed)
                return;

            if (disposing)
            {
                _logger.LogInformation("Disposing DataContext...");

                try
                {
                    // Rollback any active transaction;
                    if (_currentTransaction != null)
                    {
                        _currentTransaction.Rollback();
                        DisposeTransaction();
                    }

                    // Close connection;
                    if (ConnectionState == ConnectionState.Open)
                    {
                        Database.CloseConnection();
                        ConnectionState = ConnectionState.Closed;
                    }

                    // Dispose base context;
                    base.Dispose();

                    // Dispose semaphore;
                    _migrationSemaphore.Dispose();

                    _isDisposed = true;

                    _logger.LogInformation("DataContext disposed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during DataContext disposal");
                }
            }
        }

        /// <summary>
        /// Async dispose pattern;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Async dispose core implementation;
        /// </summary>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            if (_isDisposed)
                return;

            try
            {
                // Rollback any active transaction;
                if (_currentTransaction != null)
                {
                    await _currentTransaction.RollbackAsync();
                    await _currentTransaction.DisposeAsync();
                    _currentTransaction = null;
                }

                // Close connection;
                if (ConnectionState == ConnectionState.Open)
                {
                    await Database.CloseConnectionAsync();
                    ConnectionState = ConnectionState.Closed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during async disposal of DataContext");
            }
        }

        /// <summary>
        /// Finalizer;
        /// </summary>
        ~DataContext()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Interfaces and Classes;

    /// <summary>
    /// Interface for data context operations;
    /// </summary>
    public interface IDataContext : IDisposable, IAsyncDisposable;
    {
        // Properties;
        ConnectionState ConnectionState { get; }
        bool HasActiveTransaction { get; }
        string CurrentTenantId { get; }
        DatabaseConfig Configuration { get; }
        DatabaseStatistics Statistics { get; }

        // DbSets (partial list for interface)
        DbSet<KnowledgeArticle> KnowledgeArticles { get; set; }
        DbSet<ConversationSession> ConversationSessions { get; set; }
        DbSet<TrainingDataset> TrainingDatasets { get; set; }
        DbSet<UserInteraction> UserInteractions { get; set; }
        DbSet<SystemSetting> SystemSettings { get; set; }

        // Connection Management;
        Task OpenConnectionAsync(CancellationToken cancellationToken = default);
        Task CloseConnectionAsync();
        Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
        Task MigrateAsync(CancellationToken cancellationToken = default);

        // Transaction Management;
        Task<IDbContextTransaction> BeginTransactionAsync(
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted);
        Task CommitTransactionAsync();
        Task RollbackTransactionAsync();
        Task<T> ExecuteInTransactionAsync<T>(
            Func<Task<T>> operation,
            System.Data.IsolationLevel isolationLevel = System.Data.IsolationLevel.ReadCommitted);

        // Save Operations;
        Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
        Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default);

        // Query Operations;
        Task<IEnumerable<T>> QueryAsync<T>(string sql, params object[] parameters) where T : class;
        Task<int> ExecuteStoredProcedureAsync(string procedureName, params object[] parameters);
        Func<DataContext, IAsyncEnumerable<T>> CompileQuery<T>(
            Expression<Func<DataContext, IQueryable<T>>> queryExpression);

        // Bulk Operations;
        Task BulkInsertAsync<T>(IEnumerable<T> entities, CancellationToken cancellationToken = default) where T : class;
        Task BulkUpdateAsync<T>(IEnumerable<T> entities, Action<T> updateAction, CancellationToken cancellationToken = default) where T : class;
        Task BulkDeleteAsync<T>(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default) where T : class;

        // Diagnostics and Maintenance;
        Task<DatabaseDiagnostics> GetDiagnosticsAsync(CancellationToken cancellationToken = default);
        void ClearChangeTracker();
        Task ExecuteMaintenanceAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Database connection state enumeration;
    /// </summary>
    public enum ConnectionState;
    {
        Closed,
        Open,
        Connecting,
        Executing,
        Broken;
    }

    /// <summary>
    /// Database provider enumeration;
    /// </summary>
    public enum DatabaseProvider;
    {
        SqlServer,
        PostgreSQL,
        SQLite,
        MySQL,
        Oracle,
        CosmosDb;
    }

    /// <summary>
    /// Database statistics tracking;
    /// </summary>
    public class DatabaseStatistics;
    {
        public long ConnectionsOpened { get; private set; }
        public long ConnectionsClosed { get; private set; }
        public long TransactionsStarted { get; private set; }
        public long TransactionsCommitted { get; private set; }
        public long TransactionsRolledBack { get; private set; }
        public long QueriesExecuted { get; private set; }
        public long StoredProceduresExecuted { get; private set; }
        public long ChangesSaved { get; private set; }
        public long BulkInserts { get; private set; }
        public long BulkUpdates { get; private set; }
        public long BulkDeletes { get; private set; }
        public DateTime StartTime { get; private set; }
        public DateTime LastOperationTime { get; private set; }

        public DatabaseStatistics()
        {
            StartTime = DateTime.UtcNow;
            LastOperationTime = DateTime.UtcNow;
        }

        public void ConnectionOpened() { ConnectionsOpened++; UpdateLastOperation(); }
        public void ConnectionClosed() { ConnectionsClosed++; UpdateLastOperation(); }
        public void TransactionStarted() { TransactionsStarted++; UpdateLastOperation(); }
        public void TransactionCommitted() { TransactionsCommitted++; UpdateLastOperation(); }
        public void TransactionRolledBack() { TransactionsRolledBack++; UpdateLastOperation(); }
        public void QueryExecuted() { QueriesExecuted++; UpdateLastOperation(); }
        public void StoredProcedureExecuted() { StoredProceduresExecuted++; UpdateLastOperation(); }
        public void ChangesSaved(long count) { ChangesSaved += count; UpdateLastOperation(); }
        public void BulkInsertPerformed(long count) { BulkInserts += count; UpdateLastOperation(); }
        public void BulkUpdatePerformed(long count) { BulkUpdates += count; UpdateLastOperation(); }
        public void BulkDeletePerformed(long count) { BulkDeletes += count; UpdateLastOperation(); }

        private void UpdateLastOperation() => LastOperationTime = DateTime.UtcNow;

        public DatabaseStatistics Clone()
        {
            return new DatabaseStatistics;
            {
                ConnectionsOpened = this.ConnectionsOpened,
                ConnectionsClosed = this.ConnectionsClosed,
                TransactionsStarted = this.TransactionsStarted,
                TransactionsCommitted = this.TransactionsCommitted,
                TransactionsRolledBack = this.TransactionsRolledBack,
                QueriesExecuted = this.QueriesExecuted,
                StoredProceduresExecuted = this.StoredProceduresExecuted,
                ChangesSaved = this.ChangesSaved,
                BulkInserts = this.BulkInserts,
                BulkUpdates = this.BulkUpdates,
                BulkDeletes = this.BulkDeletes,
                StartTime = this.StartTime,
                LastOperationTime = this.LastOperationTime;
            };
        }
    }

    #endregion;
}
