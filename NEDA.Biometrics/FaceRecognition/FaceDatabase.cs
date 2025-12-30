using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NEDA.API.Middleware;
using NEDA.API.Versioning;
using NEDA.CharacterSystems.CharacterCreator.SkeletonSetup;
using NEDA.Common;
using NEDA.Common.Utilities;
using NEDA.Core.ExceptionHandling.ErrorCodes;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.Services.Messaging.EventBus;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using static NEDA.API.Middleware.LoggingMiddleware;
using static NEDA.Biometrics.FaceRecognition.FaceDatabase;
using static System.Net.Mime.MediaTypeNames;

namespace NEDA.Biometrics.FaceRecognition;
{
    /// <summary>
    /// İleri Seviye Yüz Veritabanı Yönetim Sistemi;
    /// Özellikler: Güvenli depolama, hızlı arama, şifreleme, yedekleme;
    /// Tasarım desenleri: Repository, Unit of Work, Observer, Strategy;
    /// </summary>
    public class FaceDatabase : IFaceDatabase, IDisposable;
    {
        #region Fields;

        private readonly ILogger _logger;
        private readonly IEventBus _eventBus;
        private readonly IErrorReporter _errorReporter;
        private readonly IPerformanceMonitor _performanceMonitor;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IKeyManager _keyManager;

        private FaceDbContext _dbContext;
        private DatabaseConfiguration _configuration;
        private DatabaseState _state;
        private readonly ConcurrentDictionary<string, FaceCacheEntry> _faceCache;
        private readonly ConcurrentDictionary<Guid, PersonCacheEntry> _personCache;
        private readonly IndexManager _indexManager;
        private readonly SearchEngine _searchEngine;
        private readonly EncryptionService _encryptionService;
        private readonly BackupService _backupService;
        private readonly ValidationService _validationService;
        private readonly CompressionService _compressionService;
        private readonly SynchronizationService _syncService;
        private readonly MaintenanceService _maintenanceService;
        private readonly CancellationTokenSource _cancellationTokenSource;

        private bool _isInitialized;
        private bool _isMaintenanceRunning;
        private bool _isBackupRunning;
        private DateTime _lastBackupTime;
        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _dbSemaphore;
        private readonly List<DatabaseMetric> _performanceMetrics;

        #endregion;

        #region Properties;

        /// <summary>
        /// Veritabanı durumu;
        /// </summary>
        public DatabaseState State;
        {
            get => _state;
            private set;
            {
                if (_state != value)
                {
                    _state = value;
                    OnStateChanged();
                }
            }
        }

        /// <summary>
        /// Konfigürasyon;
        /// </summary>
        public DatabaseConfiguration Configuration;
        {
            get => _configuration;
            private set;
            {
                _configuration = value ?? throw new ArgumentNullException(nameof(value));
                OnConfigurationChanged();
            }
        }

        /// <summary>
        /// Başlatıldı mı?
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Veritabanı yolu;
        /// </summary>
        public string DatabasePath => _configuration?.DatabasePath;

        /// <summary>
        /// Toplam kişi sayısı;
        /// </summary>
        public int TotalPersons { get; private set; }

        /// <summary>
        /// Toplam yüz sayısı;
        /// </summary>
        public int TotalFaces { get; private set; }

        /// <summary>
        /// Veritabanı boyutu (MB)
        /// </summary>
        public double DatabaseSizeMB { get; private set; }

        /// <summary>
        /// Cache kullanımı (MB)
        /// </summary>
        public double CacheSizeMB { get; private set; }

        /// <summary>
        /// Son yedekleme zamanı;
        /// </summary>
        public DateTime LastBackupTime => _lastBackupTime;

        /// <summary>
        /// Performans metrikleri;
        /// </summary>
        public IReadOnlyList<DatabaseMetric> PerformanceMetrics => _performanceMetrics;

        /// <summary>
        /// Cache istatistikleri;
        /// </summary>
        public CacheStatistics CacheStatistics { get; private set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Kişi eklendi event'i;
        /// </summary>
        public event EventHandler<PersonAddedEventArgs> PersonAdded;

        /// <summary>
        /// Yüz eklendi event'i;
        /// </summary>
        public event EventHandler<FaceAddedEventArgs> FaceAdded;

        /// <summary>
        /// Kişi silindi event'i;
        /// </summary>
        public event EventHandler<PersonDeletedEventArgs> PersonDeleted;

        /// <summary>
        /// Yüz silindi event'i;
        /// </summary>
        public event EventHandler<FaceDeletedEventArgs> FaceDeleted;

        /// <summary>
        /// Veritabanı yedeklendi event'i;
        /// </summary>
        public event EventHandler<DatabaseBackupCompletedEventArgs> BackupCompleted;

        /// <summary>
        /// Veritabanı optimize edildi event'i;
        /// </summary>
        public event EventHandler<DatabaseOptimizedEventArgs> DatabaseOptimized;

        /// <summary>
        /// Durum değişti event'i;
        /// </summary>
        public event EventHandler<DatabaseStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Veritabanı temizlendi event'i;
        /// </summary>
        public event EventHandler<DatabaseCleanedEventArgs> DatabaseCleaned;

        #endregion;

        #region Constructor;

        /// <summary>
        /// FaceDatabase constructor;
        /// </summary>
        public FaceDatabase(
            ILogger logger,
            IEventBus eventBus,
            IErrorReporter errorReporter,
            IPerformanceMonitor performanceMonitor,
            ICryptoEngine cryptoEngine,
            IKeyManager keyManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _performanceMonitor = performanceMonitor ?? throw new ArgumentNullException(nameof(performanceMonitor));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _keyManager = keyManager ?? throw new ArgumentNullException(nameof(keyManager));

            // Cache'leri başlat;
            _faceCache = new ConcurrentDictionary<string, FaceCacheEntry>();
            _personCache = new ConcurrentDictionary<Guid, PersonCacheEntry>();

            // Servisleri başlat;
            _indexManager = new IndexManager(logger);
            _searchEngine = new SearchEngine(logger);
            _encryptionService = new EncryptionService(logger, cryptoEngine, keyManager);
            _backupService = new BackupService(logger);
            _validationService = new ValidationService(logger);
            _compressionService = new CompressionService(logger);
            _syncService = new SynchronizationService(logger);
            _maintenanceService = new MaintenanceService(logger);

            _cancellationTokenSource = new CancellationTokenSource();
            _dbSemaphore = new SemaphoreSlim(1, 1);
            _performanceMetrics = new List<DatabaseMetric>();
            CacheStatistics = new CacheStatistics();

            // Varsayılan konfigürasyon;
            _configuration = DatabaseConfiguration.Default;

            State = DatabaseState.Closed;

            _logger.LogInformation("FaceDatabase initialized successfully", GetType());
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Yüz veritabanını başlat;
        /// </summary>
        public async Task InitializeAsync(DatabaseConfiguration configuration = null)
        {
            try
            {
                _performanceMonitor.StartOperation("InitializeFaceDatabase");
                _logger.LogDebug("Initializing face database");

                if (_isInitialized)
                {
                    _logger.LogWarning("Face database is already initialized");
                    return;
                }

                // Konfigürasyonu ayarla;
                if (configuration != null)
                {
                    Configuration = configuration;
                }

                // Veritabanı dizinini oluştur;
                CreateDatabaseDirectory();

                // Veritabanı bağlantısını oluştur;
                await InitializeDbContextAsync();

                // Migrations uygula;
                await ApplyMigrationsAsync();

                // Index'leri oluştur;
                await _indexManager.CreateIndexesAsync(_dbContext);

                // Cache'i temizle;
                ClearCaches();

                // Servisleri başlat;
                await InitializeServicesAsync();

                // İstatistikleri güncelle;
                await UpdateStatisticsAsync();

                _isInitialized = true;
                State = DatabaseState.Open;

                // Bakım görevini başlat;
                StartMaintenanceTask();

                _logger.LogInformation("Face database initialized successfully");
                _eventBus.Publish(new FaceDatabaseInitializedEvent(this));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize face database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.InitializationFailed);
                throw new FaceDatabaseException("Failed to initialize face database", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("InitializeFaceDatabase");
            }
        }

        /// <summary>
        /// Kişi ekle;
        /// </summary>
        public async Task<PersonRecord> AddPersonAsync(PersonData personData)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("AddPerson");
                _logger.LogDebug($"Adding person: {personData.Name}");

                await _dbSemaphore.WaitAsync();

                try
                {
                    // Kişi doğrulaması yap;
                    await _validationService.ValidatePersonAsync(personData);

                    // Kişi entity'si oluştur;
                    var personEntity = new PersonEntity;
                    {
                        Id = Guid.NewGuid(),
                        Name = personData.Name,
                        Surname = personData.Surname,
                        Email = personData.Email,
                        Phone = personData.Phone,
                        DateOfBirth = personData.DateOfBirth,
                        Gender = personData.Gender,
                        NationalId = personData.NationalId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow,
                        IsActive = true,
                        Metadata = SerializeMetadata(personData.Metadata)
                    };

                    // Hassas verileri şifrele;
                    personEntity = await _encryptionService.EncryptPersonDataAsync(personEntity);

                    // Veritabanına ekle;
                    await _dbContext.Persons.AddAsync(personEntity);
                    await _dbContext.SaveChangesAsync();

                    // Cache'e ekle;
                    var cacheEntry = new PersonCacheEntry
                    {
                        Person = personEntity,
                        LastAccess = DateTime.UtcNow,
                        AccessCount = 1;
                    };
                    _personCache[personEntity.Id] = cacheEntry

                    // PersonRecord oluştur;
                    var personRecord = CreatePersonRecord(personEntity);

                    // İstatistikleri güncelle;
                    TotalPersons++;
                    UpdateCacheStatistics();

                    // Event tetikle;
                    OnPersonAdded(personRecord);

                    _logger.LogInformation($"Person added successfully: {personData.Name} (ID: {personEntity.Id})");

                    return personRecord;
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add person");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.AddPersonFailed);
                throw new FaceDatabaseException("Failed to add person", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AddPerson");
            }
        }

        /// <summary>
        /// Yüz ekle;
        /// </summary>
        public async Task<FaceRecord> AddFaceAsync(Guid personId, FaceData faceData)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("AddFace");
                _logger.LogDebug($"Adding face for person: {personId}");

                // Kişiyi bul;
                var person = await GetPersonEntityAsync(personId);
                if (person == null)
                {
                    throw new PersonNotFoundException($"Person not found: {personId}");
                }

                // Yüz doğrulaması yap;
                await _validationService.ValidateFaceAsync(faceData);

                // Yüz özelliklerini çıkar;
                var faceFeatures = await ExtractFaceFeaturesAsync(faceData);

                // Yüz entity'si oluştur;
                var faceEntity = new FaceEntity;
                {
                    Id = Guid.NewGuid(),
                    PersonId = personId,
                    ImageHash = ComputeImageHash(faceData.ImageData),
                    FeatureVector = faceFeatures,
                    ImageQuality = faceData.QualityScore,
                    Pose = faceData.Pose,
                    Expression = faceData.Expression,
                    Lighting = faceData.Lighting,
                    CreatedAt = DateTime.UtcNow,
                    IsActive = true,
                    IsPrimary = await ShouldSetAsPrimaryAsync(personId),
                    Metadata = SerializeMetadata(faceData.Metadata)
                };

                // Yüz verilerini şifrele;
                faceEntity = await _encryptionService.EncryptFaceDataAsync(faceEntity);

                await _dbSemaphore.WaitAsync();

                try
                {
                    // Veritabanına ekle;
                    await _dbContext.Faces.AddAsync(faceEntity);

                    // Kişinin yüz sayısını güncelle;
                    person.FaceCount++;
                    person.UpdatedAt = DateTime.UtcNow;

                    await _dbContext.SaveChangesAsync();

                    // Cache'e ekle;
                    var cacheKey = GenerateFaceCacheKey(faceEntity.Id);
                    var cacheEntry = new FaceCacheEntry
                    {
                        Face = faceEntity,
                        LastAccess = DateTime.UtcNow,
                        AccessCount = 1,
                        FeatureVector = faceFeatures;
                    };
                    _faceCache[cacheKey] = cacheEntry

                    // FaceRecord oluştur;
                    var faceRecord = CreateFaceRecord(faceEntity, person);

                    // Index'i güncelle;
                    await _indexManager.AddToIndexAsync(faceEntity, faceFeatures);

                    // İstatistikleri güncelle;
                    TotalFaces++;
                    UpdateCacheStatistics();

                    // Event tetikle;
                    OnFaceAdded(faceRecord);

                    _logger.LogInformation($"Face added successfully for person: {personId} (Face ID: {faceEntity.Id})");

                    return faceRecord;
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add face");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.AddFaceFailed);
                throw new FaceDatabaseException("Failed to add face", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AddFace");
            }
        }

        /// <summary>
        /// Batch yüz ekle;
        /// </summary>
        public async Task<BatchFaceResult> AddFacesBatchAsync(Guid personId, IEnumerable<FaceData> facesData)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("AddFacesBatch");
                _logger.LogDebug($"Adding batch of {facesData.Count()} faces for person: {personId}");

                // Kişiyi bul;
                var person = await GetPersonEntityAsync(personId);
                if (person == null)
                {
                    throw new PersonNotFoundException($"Person not found: {personId}");
                }

                var faceRecords = new List<FaceRecord>();
                var failedFaces = new List<FailedFace>();

                await _dbSemaphore.WaitAsync();

                try
                {
                    foreach (var faceData in facesData)
                    {
                        try
                        {
                            // Yüz doğrulaması yap;
                            await _validationService.ValidateFaceAsync(faceData);

                            // Yüz özelliklerini çıkar;
                            var faceFeatures = await ExtractFaceFeaturesAsync(faceData);

                            // Yüz entity'si oluştur;
                            var faceEntity = new FaceEntity;
                            {
                                Id = Guid.NewGuid(),
                                PersonId = personId,
                                ImageHash = ComputeImageHash(faceData.ImageData),
                                FeatureVector = faceFeatures,
                                ImageQuality = faceData.QualityScore,
                                Pose = faceData.Pose,
                                Expression = faceData.Expression,
                                Lighting = faceData.Lighting,
                                CreatedAt = DateTime.UtcNow,
                                IsActive = true,
                                IsPrimary = false, // Batch'te primary olmaz;
                                Metadata = SerializeMetadata(faceData.Metadata)
                            };

                            // Yüz verilerini şifrele;
                            faceEntity = await _encryptionService.EncryptFaceDataAsync(faceEntity);

                            // Veritabanına ekle;
                            await _dbContext.Faces.AddAsync(faceEntity);

                            // Cache'e ekle;
                            var cacheKey = GenerateFaceCacheKey(faceEntity.Id);
                            var cacheEntry = new FaceCacheEntry
                            {
                                Face = faceEntity,
                                LastAccess = DateTime.UtcNow,
                                AccessCount = 1,
                                FeatureVector = faceFeatures;
                            };
                            _faceCache[cacheKey] = cacheEntry

                            // FaceRecord oluştur;
                            var faceRecord = CreateFaceRecord(faceEntity, person);
                            faceRecords.Add(faceRecord);

                            // Index'i güncelle;
                            await _indexManager.AddToIndexAsync(faceEntity, faceFeatures);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, $"Failed to add face in batch for person: {personId}");
                            failedFaces.Add(new FailedFace;
                            {
                                FaceData = faceData,
                                Error = ex.Message,
                                Timestamp = DateTime.UtcNow;
                            });
                        }
                    }

                    // Kişinin yüz sayısını güncelle;
                    person.FaceCount += faceRecords.Count;
                    person.UpdatedAt = DateTime.UtcNow;

                    await _dbContext.SaveChangesAsync();

                    // İstatistikleri güncelle;
                    TotalFaces += faceRecords.Count;
                    UpdateCacheStatistics();

                    _logger.LogInformation($"Batch faces added: {faceRecords.Count} successful, {failedFaces.Count} failed");

                    return new BatchFaceResult;
                    {
                        PersonId = personId,
                        SuccessfulCount = faceRecords.Count,
                        FailedCount = failedFaces.Count,
                        FaceRecords = faceRecords,
                        FailedFaces = failedFaces,
                        Timestamp = DateTime.UtcNow;
                    };
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to add faces batch");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.AddFacesBatchFailed);
                throw new FaceDatabaseException("Failed to add faces batch", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("AddFacesBatch");
            }
        }

        /// <summary>
        /// Kişi getir;
        /// </summary>
        public async Task<PersonRecord> GetPersonAsync(Guid personId)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("GetPerson");
                _logger.LogDebug($"Getting person: {personId}");

                // Cache'den kontrol et;
                if (_personCache.TryGetValue(personId, out var cacheEntry))
                {
                    cacheEntry.LastAccess = DateTime.UtcNow;
                    cacheEntry.AccessCount++;

                    _logger.LogDebug($"Person found in cache: {personId}");
                    return CreatePersonRecord(cacheEntry.Person);
                }

                // Veritabanından getir;
                var personEntity = await GetPersonEntityAsync(personId);
                if (personEntity == null)
                {
                    _logger.LogWarning($"Person not found: {personId}");
                    return null;
                }

                // Şifreyi çöz;
                personEntity = await _encryptionService.DecryptPersonDataAsync(personEntity);

                // Cache'e ekle;
                var newCacheEntry = new PersonCacheEntry
                {
                    Person = personEntity,
                    LastAccess = DateTime.UtcNow,
                    AccessCount = 1;
                };
                _personCache[personId] = newCacheEntry

                UpdateCacheStatistics();

                return CreatePersonRecord(personEntity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get person: {personId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.GetPersonFailed);
                throw new FaceDatabaseException($"Failed to get person: {personId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("GetPerson");
            }
        }

        /// <summary>
        /// Yüz getir;
        /// </summary>
        public async Task<FaceRecord> GetFaceAsync(Guid faceId)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("GetFace");
                _logger.LogDebug($"Getting face: {faceId}");

                var cacheKey = GenerateFaceCacheKey(faceId);

                // Cache'den kontrol et;
                if (_faceCache.TryGetValue(cacheKey, out var cacheEntry))
                {
                    cacheEntry.LastAccess = DateTime.UtcNow;
                    cacheEntry.AccessCount++;

                    _logger.LogDebug($"Face found in cache: {faceId}");

                    // Kişiyi getir;
                    var person = await GetPersonAsync(cacheEntry.Face.PersonId);
                    return CreateFaceRecord(cacheEntry.Face, person?.ToPersonEntity());
                }

                // Veritabanından getir;
                var faceEntity = await GetFaceEntityAsync(faceId);
                if (faceEntity == null)
                {
                    _logger.LogWarning($"Face not found: {faceId}");
                    return null;
                }

                // Şifreyi çöz;
                faceEntity = await _encryptionService.DecryptFaceDataAsync(faceEntity);

                // Kişiyi getir;
                var personEntity = await GetPersonEntityAsync(faceEntity.PersonId);

                // Cache'e ekle;
                var newCacheEntry = new FaceCacheEntry
                {
                    Face = faceEntity,
                    LastAccess = DateTime.UtcNow,
                    AccessCount = 1;
                };
                _faceCache[cacheKey] = newCacheEntry

                UpdateCacheStatistics();

                return CreateFaceRecord(faceEntity, personEntity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get face: {faceId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.GetFaceFailed);
                throw new FaceDatabaseException($"Failed to get face: {faceId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("GetFace");
            }
        }

        /// <summary>
        /// Kişinin tüm yüzlerini getir;
        /// </summary>
        public async Task<IEnumerable<FaceRecord>> GetPersonFacesAsync(Guid personId)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("GetPersonFaces");
                _logger.LogDebug($"Getting faces for person: {personId}");

                // Kişiyi kontrol et;
                var person = await GetPersonAsync(personId);
                if (person == null)
                {
                    throw new PersonNotFoundException($"Person not found: {personId}");
                }

                // Veritabanından getir;
                var faceEntities = await _dbContext.Faces;
                    .Where(f => f.PersonId == personId && f.IsActive)
                    .OrderByDescending(f => f.IsPrimary)
                    .ThenByDescending(f => f.ImageQuality)
                    .ThenByDescending(f => f.CreatedAt)
                    .ToListAsync();

                var faceRecords = new List<FaceRecord>();

                foreach (var faceEntity in faceEntities)
                {
                    // Şifreyi çöz;
                    var decryptedFace = await _encryptionService.DecryptFaceDataAsync(faceEntity);

                    // Cache'e ekle;
                    var cacheKey = GenerateFaceCacheKey(faceEntity.Id);
                    if (!_faceCache.ContainsKey(cacheKey))
                    {
                        _faceCache[cacheKey] = new FaceCacheEntry
                        {
                            Face = decryptedFace,
                            LastAccess = DateTime.UtcNow,
                            AccessCount = 1;
                        };
                    }

                    faceRecords.Add(CreateFaceRecord(decryptedFace, person.ToPersonEntity()));
                }

                UpdateCacheStatistics();

                _logger.LogDebug($"Found {faceRecords.Count} faces for person: {personId}");

                return faceRecords;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get person faces: {personId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.GetPersonFacesFailed);
                throw new FaceDatabaseException($"Failed to get person faces: {personId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("GetPersonFaces");
            }
        }

        /// <summary>
        /// Yüz arama yap;
        /// </summary>
        public async Task<IEnumerable<FaceSearchResult>> SearchFacesAsync(
            FaceSearchCriteria criteria,
            int maxResults = 10)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("SearchFaces");
                _logger.LogDebug($"Searching faces with criteria, max results: {maxResults}");

                // Arama motoru ile arama yap;
                var searchResults = await _searchEngine.SearchAsync(
                    _dbContext,
                    criteria,
                    maxResults,
                    _cancellationTokenSource.Token);

                var results = new List<FaceSearchResult>();

                foreach (var searchResult in searchResults)
                {
                    // Yüz ve kişi bilgilerini getir;
                    var faceRecord = await GetFaceAsync(searchResult.FaceId);
                    var personRecord = faceRecord != null ? await GetPersonAsync(faceRecord.PersonId) : null;

                    if (faceRecord != null && personRecord != null)
                    {
                        results.Add(new FaceSearchResult;
                        {
                            FaceRecord = faceRecord,
                            PersonRecord = personRecord,
                            MatchScore = searchResult.MatchScore,
                            Similarity = searchResult.Similarity,
                            Distance = searchResult.Distance,
                            MatchedFeatures = searchResult.MatchedFeatures;
                        });
                    }
                }

                _logger.LogInformation($"Face search completed: {results.Count} results found");

                return results;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to search faces");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.SearchFacesFailed);
                throw new FaceDatabaseException("Failed to search faces", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("SearchFaces");
            }
        }

        /// <summary>
        /// Yüz tanıma yap;
        /// </summary>
        public async Task<FaceRecognitionResult> RecognizeFaceAsync(
            byte[] faceImage,
            double confidenceThreshold = 0.8,
            int maxCandidates = 5)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("RecognizeFace");
                _logger.LogDebug($"Recognizing face, confidence threshold: {confidenceThreshold}");

                // Yüz özelliklerini çıkar;
                var faceFeatures = await ExtractFaceFeaturesAsync(new FaceData;
                {
                    ImageData = faceImage,
                    QualityScore = CalculateImageQuality(faceImage)
                });

                // Arama kriterleri oluştur;
                var searchCriteria = new FaceSearchCriteria;
                {
                    FeatureVector = faceFeatures,
                    MinConfidence = confidenceThreshold,
                    MaxResults = maxCandidates,
                    SearchMode = SearchMode.Recognition;
                };

                // Arama yap;
                var searchResults = await SearchFacesAsync(searchCriteria, maxCandidates);

                // Sonuçları analiz et;
                var recognitionResult = new FaceRecognitionResult;
                {
                    QueryFeatures = faceFeatures,
                    Timestamp = DateTime.UtcNow,
                    Candidates = new List<RecognitionCandidate>()
                };

                foreach (var result in searchResults)
                {
                    if (result.MatchScore >= confidenceThreshold)
                    {
                        recognitionResult.Candidates.Add(new RecognitionCandidate;
                        {
                            PersonRecord = result.PersonRecord,
                            FaceRecord = result.FaceRecord,
                            Confidence = result.MatchScore,
                            Similarity = result.Similarity,
                            Distance = result.Distance;
                        });
                    }
                }

                // En iyi eşleşmeyi belirle;
                if (recognitionResult.Candidates.Any())
                {
                    var bestMatch = recognitionResult.Candidates;
                        .OrderByDescending(c => c.Confidence)
                        .First();

                    recognitionResult.BestMatch = bestMatch;
                    recognitionResult.IsRecognized = bestMatch.Confidence >= confidenceThreshold;
                }

                _logger.LogInformation($"Face recognition completed: {recognitionResult.Candidates.Count} candidates found");

                return recognitionResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recognize face");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.RecognizeFaceFailed);
                throw new FaceDatabaseException("Failed to recognize face", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("RecognizeFace");
            }
        }

        /// <summary>
        /// Kişi güncelle;
        /// </summary>
        public async Task<PersonRecord> UpdatePersonAsync(Guid personId, PersonUpdateData updateData)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("UpdatePerson");
                _logger.LogDebug($"Updating person: {personId}");

                await _dbSemaphore.WaitAsync();

                try
                {
                    // Kişiyi bul;
                    var personEntity = await GetPersonEntityAsync(personId);
                    if (personEntity == null)
                    {
                        throw new PersonNotFoundException($"Person not found: {personId}");
                    }

                    // Güncelleme doğrulaması yap;
                    await _validationService.ValidatePersonUpdateAsync(updateData);

                    // Alanları güncelle;
                    if (!string.IsNullOrEmpty(updateData.Name))
                        personEntity.Name = updateData.Name;

                    if (!string.IsNullOrEmpty(updateData.Surname))
                        personEntity.Surname = updateData.Surname;

                    if (!string.IsNullOrEmpty(updateData.Email))
                        personEntity.Email = updateData.Email;

                    if (!string.IsNullOrEmpty(updateData.Phone))
                        personEntity.Phone = updateData.Phone;

                    if (updateData.DateOfBirth.HasValue)
                        personEntity.DateOfBirth = updateData.DateOfBirth.Value;

                    if (updateData.Gender.HasValue)
                        personEntity.Gender = updateData.Gender.Value;

                    if (updateData.IsActive.HasValue)
                        personEntity.IsActive = updateData.IsActive.Value;

                    if (updateData.Metadata != null)
                        personEntity.Metadata = SerializeMetadata(updateData.Metadata);

                    personEntity.UpdatedAt = DateTime.UtcNow;

                    // Şifrele;
                    personEntity = await _encryptionService.EncryptPersonDataAsync(personEntity);

                    // Veritabanını güncelle;
                    _dbContext.Persons.Update(personEntity);
                    await _dbContext.SaveChangesAsync();

                    // Cache'i güncelle;
                    if (_personCache.TryGetValue(personId, out var cacheEntry))
                    {
                        cacheEntry.Person = personEntity;
                        cacheEntry.LastAccess = DateTime.UtcNow;
                    }

                    // PersonRecord oluştur;
                    var personRecord = CreatePersonRecord(personEntity);

                    _logger.LogInformation($"Person updated successfully: {personId}");

                    return personRecord;
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update person: {personId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.UpdatePersonFailed);
                throw new FaceDatabaseException($"Failed to update person: {personId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("UpdatePerson");
            }
        }

        /// <summary>
        /// Yüz güncelle;
        /// </summary>
        public async Task<FaceRecord> UpdateFaceAsync(Guid faceId, FaceUpdateData updateData)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("UpdateFace");
                _logger.LogDebug($"Updating face: {faceId}");

                await _dbSemaphore.WaitAsync();

                try
                {
                    // Yüzü bul;
                    var faceEntity = await GetFaceEntityAsync(faceId);
                    if (faceEntity == null)
                    {
                        throw new FaceNotFoundException($"Face not found: {faceId}");
                    }

                    // Güncelleme doğrulaması yap;
                    await _validationService.ValidateFaceUpdateAsync(updateData);

                    // Alanları güncelle;
                    if (updateData.IsPrimary.HasValue && updateData.IsPrimary.Value)
                    {
                        // Diğer yüzlerin primary flag'ini kaldır;
                        await RemovePrimaryFlagFromOtherFacesAsync(faceEntity.PersonId);
                        faceEntity.IsPrimary = true;
                    }
                    else if (updateData.IsPrimary.HasValue)
                    {
                        faceEntity.IsPrimary = updateData.IsPrimary.Value;
                    }

                    if (updateData.IsActive.HasValue)
                        faceEntity.IsActive = updateData.IsActive.Value;

                    if (updateData.Metadata != null)
                        faceEntity.Metadata = SerializeMetadata(updateData.Metadata);

                    // Şifrele;
                    faceEntity = await _encryptionService.EncryptFaceDataAsync(faceEntity);

                    // Veritabanını güncelle;
                    _dbContext.Faces.Update(faceEntity);
                    await _dbContext.SaveChangesAsync();

                    // Cache'i güncelle;
                    var cacheKey = GenerateFaceCacheKey(faceId);
                    if (_faceCache.TryGetValue(cacheKey, out var cacheEntry))
                    {
                        cacheEntry.Face = faceEntity;
                        cacheEntry.LastAccess = DateTime.UtcNow;
                    }

                    // Kişiyi getir;
                    var personEntity = await GetPersonEntityAsync(faceEntity.PersonId);

                    // FaceRecord oluştur;
                    var faceRecord = CreateFaceRecord(faceEntity, personEntity);

                    _logger.LogInformation($"Face updated successfully: {faceId}");

                    return faceRecord;
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update face: {faceId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.UpdateFaceFailed);
                throw new FaceDatabaseException($"Failed to update face: {faceId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("UpdateFace");
            }
        }

        /// <summary>
        /// Kişi sil;
        /// </summary>
        public async Task<bool> DeletePersonAsync(Guid personId, bool softDelete = true)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("DeletePerson");
                _logger.LogDebug($"Deleting person: {personId}, soft delete: {softDelete}");

                await _dbSemaphore.WaitAsync();

                try
                {
                    // Kişiyi bul;
                    var personEntity = await GetPersonEntityAsync(personId);
                    if (personEntity == null)
                    {
                        _logger.LogWarning($"Person not found for deletion: {personId}");
                        return false;
                    }

                    if (softDelete)
                    {
                        // Soft delete: IsActive = false;
                        personEntity.IsActive = false;
                        personEntity.UpdatedAt = DateTime.UtcNow;

                        // Kişinin yüzlerini da soft delete yap;
                        var faces = await _dbContext.Faces;
                            .Where(f => f.PersonId == personId && f.IsActive)
                            .ToListAsync();

                        foreach (var face in faces)
                        {
                            face.IsActive = false;
                        }

                        _dbContext.Persons.Update(personEntity);
                        await _dbContext.SaveChangesAsync();

                        _logger.LogInformation($"Person soft deleted: {personId}");
                    }
                    else;
                    {
                        // Hard delete: Veritabanından tamamen sil;
                        // Önce yüzleri sil;
                        var faces = await _dbContext.Faces;
                            .Where(f => f.PersonId == personId)
                            .ToListAsync();

                        _dbContext.Faces.RemoveRange(faces);

                        // Sonra kişiyi sil;
                        _dbContext.Persons.Remove(personEntity);

                        await _dbContext.SaveChangesAsync();

                        // Index'lerden kaldır;
                        foreach (var face in faces)
                        {
                            await _indexManager.RemoveFromIndexAsync(face);
                        }

                        _logger.LogInformation($"Person hard deleted: {personId}");
                    }

                    // Cache'den kaldır;
                    _personCache.TryRemove(personId, out _);

                    // Kişinin yüz cache'lerini temizle;
                    ClearPersonFaceCache(personId);

                    // İstatistikleri güncelle;
                    TotalPersons--;
                    TotalFaces -= personEntity.FaceCount;
                    UpdateCacheStatistics();

                    // Event tetikle;
                    OnPersonDeleted(personId, softDelete);

                    return true;
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete person: {personId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.DeletePersonFailed);
                throw new FaceDatabaseException($"Failed to delete person: {personId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DeletePerson");
            }
        }

        /// <summary>
        /// Yüz sil;
        /// </summary>
        public async Task<bool> DeleteFaceAsync(Guid faceId, bool softDelete = true)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("DeleteFace");
                _logger.LogDebug($"Deleting face: {faceId}, soft delete: {softDelete}");

                await _dbSemaphore.WaitAsync();

                try
                {
                    // Yüzü bul;
                    var faceEntity = await GetFaceEntityAsync(faceId);
                    if (faceEntity == null)
                    {
                        _logger.LogWarning($"Face not found for deletion: {faceId}");
                        return false;
                    }

                    var personId = faceEntity.PersonId;
                    var wasPrimary = faceEntity.IsPrimary;

                    if (softDelete)
                    {
                        // Soft delete: IsActive = false;
                        faceEntity.IsActive = false;
                        _dbContext.Faces.Update(faceEntity);

                        _logger.LogInformation($"Face soft deleted: {faceId}");
                    }
                    else;
                    {
                        // Hard delete: Veritabanından tamamen sil;
                        _dbContext.Faces.Remove(faceEntity);

                        // Index'ten kaldır;
                        await _indexManager.RemoveFromIndexAsync(faceEntity);

                        _logger.LogInformation($"Face hard deleted: {faceId}");
                    }

                    // Kişinin yüz sayısını güncelle;
                    var personEntity = await GetPersonEntityAsync(personId);
                    if (personEntity != null)
                    {
                        personEntity.FaceCount = Math.Max(0, personEntity.FaceCount - 1);

                        // Eğer silinen yüz primary ise, yeni primary yüz seç;
                        if (wasPrimary && personEntity.FaceCount > 0)
                        {
                            await SetNewPrimaryFaceAsync(personId);
                        }

                        personEntity.UpdatedAt = DateTime.UtcNow;
                    }

                    await _dbContext.SaveChangesAsync();

                    // Cache'den kaldır;
                    var cacheKey = GenerateFaceCacheKey(faceId);
                    _faceCache.TryRemove(cacheKey, out _);

                    // İstatistikleri güncelle;
                    TotalFaces--;
                    UpdateCacheStatistics();

                    // Event tetikle;
                    OnFaceDeleted(faceId, personId, softDelete);

                    return true;
                }
                finally
                {
                    _dbSemaphore.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete face: {faceId}");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.DeleteFaceFailed);
                throw new FaceDatabaseException($"Failed to delete face: {faceId}", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DeleteFace");
            }
        }

        /// <summary>
        /// Veritabanı yedeğini al;
        /// </summary>
        public async Task<BackupResult> BackupDatabaseAsync(string backupPath = null, BackupType type = BackupType.Full)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("BackupFaceDatabase");
                _logger.LogDebug($"Backing up database, type: {type}");

                if (_isBackupRunning)
                {
                    throw new InvalidOperationException("Backup is already running");
                }

                _isBackupRunning = true;
                State = DatabaseState.BackupInProgress;

                // Yedekleme yolu belirle;
                var actualBackupPath = backupPath ?? GetDefaultBackupPath();

                // Yedekleme yap;
                var backupResult = await _backupService.BackupAsync(
                    _dbContext,
                    _configuration,
                    actualBackupPath,
                    type,
                    _cancellationTokenSource.Token);

                // İstatistikleri güncelle;
                _lastBackupTime = DateTime.UtcNow;

                // Event tetikle;
                OnBackupCompleted(backupResult);

                _logger.LogInformation($"Database backup completed: {backupResult.BackupSizeMB:F2} MB");

                return backupResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to backup database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.BackupFailed);
                throw new FaceDatabaseException("Failed to backup database", ex);
            }
            finally
            {
                _isBackupRunning = false;
                State = DatabaseState.Open;
                _performanceMonitor.EndOperation("BackupFaceDatabase");
            }
        }

        /// <summary>
        /// Veritabanı yedeğinden geri yükle;
        /// </summary>
        public async Task<RestoreResult> RestoreDatabaseAsync(string backupFilePath, bool verifyIntegrity = true)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("RestoreFaceDatabase");
                _logger.LogDebug($"Restoring database from: {backupFilePath}");

                State = DatabaseState.RestoreInProgress;

                // Veritabanını kapat;
                await CloseDatabaseAsync();

                // Geri yükleme yap;
                var restoreResult = await _backupService.RestoreAsync(
                    backupFilePath,
                    _configuration,
                    verifyIntegrity,
                    _cancellationTokenSource.Token);

                // Veritabanını yeniden başlat;
                await InitializeDbContextAsync();
                await UpdateStatisticsAsync();

                // Cache'leri temizle;
                ClearCaches();

                State = DatabaseState.Open;

                _logger.LogInformation($"Database restore completed: {restoreResult.RestoredItemCount} items restored");

                return restoreResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to restore database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.RestoreFailed);
                throw new FaceDatabaseException("Failed to restore database", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("RestoreFaceDatabase");
            }
        }

        /// <summary>
        /// Veritabanını optimize et;
        /// </summary>
        public async Task<OptimizationResult> OptimizeDatabaseAsync()
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("OptimizeFaceDatabase");
                _logger.LogDebug("Optimizing database");

                State = DatabaseState.OptimizationInProgress;

                // Optimizasyon yap;
                var optimizationResult = await _maintenanceService.OptimizeAsync(
                    _dbContext,
                    _configuration);

                // Index'leri yeniden oluştur;
                await _indexManager.RebuildIndexesAsync(_dbContext);

                // İstatistikleri güncelle;
                await UpdateStatisticsAsync();

                // Cache'leri temizle;
                ClearCaches();

                State = DatabaseState.Open;

                // Event tetikle;
                OnDatabaseOptimized(optimizationResult);

                _logger.LogInformation($"Database optimized: {optimizationResult.SpaceFreedMB:F2} MB freed");

                return optimizationResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to optimize database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.OptimizationFailed);
                throw new FaceDatabaseException("Failed to optimize database", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("OptimizeFaceDatabase");
            }
        }

        /// <summary>
        /// Veritabanını temizle (eski/inaktif kayıtlar)
        /// </summary>
        public async Task<CleanupResult> CleanupDatabaseAsync(CleanupCriteria criteria = null)
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("CleanupFaceDatabase");
                _logger.LogDebug("Cleaning up database");

                State = DatabaseState.CleanupInProgress;

                var cleanupCriteria = criteria ?? CleanupCriteria.Default;

                // Temizlik yap;
                var cleanupResult = await _maintenanceService.CleanupAsync(
                    _dbContext,
                    cleanupCriteria,
                    _cancellationTokenSource.Token);

                // İstatistikleri güncelle;
                await UpdateStatisticsAsync();

                // Cache'leri temizle;
                ClearCaches();

                State = DatabaseState.Open;

                // Event tetikle;
                OnDatabaseCleaned(cleanupResult);

                _logger.LogInformation($"Database cleaned: {cleanupResult.RemovedItems} items removed");

                return cleanupResult;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to cleanup database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.CleanupFailed);
                throw new FaceDatabaseException("Failed to cleanup database", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("CleanupFaceDatabase");
            }
        }

        /// <summary>
        /// Veritabanı istatistiklerini getir;
        /// </summary>
        public async Task<DatabaseStatistics> GetStatisticsAsync()
        {
            ValidateInitialized();

            try
            {
                _performanceMonitor.StartOperation("GetDatabaseStatistics");
                _logger.LogDebug("Getting database statistics");

                var statistics = new DatabaseStatistics;
                {
                    Timestamp = DateTime.UtcNow,
                    TotalPersons = TotalPersons,
                    TotalFaces = TotalFaces,
                    DatabaseSizeMB = DatabaseSizeMB,
                    CacheSizeMB = CacheSizeMB,
                    LastBackupTime = _lastBackupTime,
                    CacheStatistics = CacheStatistics,
                    Configuration = _configuration;
                };

                // Detaylı istatistikler;
                statistics.ActivePersons = await _dbContext.Persons.CountAsync(p => p.IsActive);
                statistics.InactivePersons = TotalPersons - statistics.ActivePersons;

                statistics.ActiveFaces = await _dbContext.Faces.CountAsync(f => f.IsActive);
                statistics.InactiveFaces = TotalFaces - statistics.ActiveFaces;

                statistics.PrimaryFaces = await _dbContext.Faces.CountAsync(f => f.IsPrimary && f.IsActive);

                // Kişi başına ortalama yüz sayısı;
                statistics.AverageFacesPerPerson = TotalPersons > 0 ? (double)TotalFaces / TotalPersons : 0;

                // Veritabanı dosya boyutu;
                statistics.DatabaseFileSizeMB = await GetDatabaseFileSizeAsync();

                // Index boyutu;
                statistics.IndexSizeMB = await _indexManager.GetIndexSizeAsync(_dbContext);

                return statistics;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get database statistics");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.GetStatisticsFailed);
                throw new FaceDatabaseException("Failed to get database statistics", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("GetDatabaseStatistics");
            }
        }

        /// <summary>
        /// Veritabanını kapat;
        /// </summary>
        public async Task CloseDatabaseAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("CloseFaceDatabase");
                _logger.LogDebug("Closing face database");

                if (!_isInitialized)
                {
                    _logger.LogWarning("Face database is not initialized");
                    return;
                }

                // Bakım görevini durdur;
                _cancellationTokenSource.Cancel();

                // Cache'leri temizle;
                ClearCaches();

                // Veritabanı bağlantısını kapat;
                if (_dbContext != null)
                {
                    await _dbContext.DisposeAsync();
                    _dbContext = null;
                }

                _isInitialized = false;
                State = DatabaseState.Closed;

                _logger.LogInformation("Face database closed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to close face database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.CloseFailed);
                throw new FaceDatabaseException("Failed to close face database", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("CloseFaceDatabase");
            }
        }

        /// <summary>
        /// Veritabanını tamamen sil;
        /// </summary>
        public async Task<bool> DeleteDatabaseAsync()
        {
            try
            {
                _performanceMonitor.StartOperation("DeleteFaceDatabase");
                _logger.LogDebug("Deleting face database");

                // Önce veritabanını kapat;
                if (_isInitialized)
                {
                    await CloseDatabaseAsync();
                }

                // Veritabanı dosyalarını sil;
                var databaseFiles = GetDatabaseFiles();
                var deletedCount = 0;

                foreach (var file in databaseFiles)
                {
                    try
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                            deletedCount++;
                            _logger.LogDebug($"Deleted database file: {file}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, $"Failed to delete database file: {file}");
                    }
                }

                // Log dosyalarını sil;
                DeleteLogFiles();

                // Cache dosyalarını sil;
                DeleteCacheFiles();

                _logger.LogInformation($"Face database deleted: {deletedCount} files removed");

                return deletedCount > 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete face database");
                _errorReporter.ReportError(ex, ErrorCodes.FaceDatabase.DeleteDatabaseFailed);
                throw new FaceDatabaseException("Failed to delete face database", ex);
            }
            finally
            {
                _performanceMonitor.EndOperation("DeleteFaceDatabase");
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Veritabanı dizinini oluştur;
        /// </summary>
        private void CreateDatabaseDirectory()
        {
            try
            {
                var databaseDir = Path.GetDirectoryName(_configuration.DatabasePath);
                if (!string.IsNullOrEmpty(databaseDir) && !Directory.Exists(databaseDir))
                {
                    Directory.CreateDirectory(databaseDir);
                    _logger.LogDebug($"Created database directory: {databaseDir}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create database directory");
                throw;
            }
        }

        /// <summary>
        /// DbContext'i başlat;
        /// </summary>
        private async Task InitializeDbContextAsync()
        {
            try
            {
                var connectionString = $"Data Source={_configuration.DatabasePath};";

                var optionsBuilder = new DbContextOptionsBuilder<FaceDbContext>()
                    .UseSqlite(connectionString)
                    .EnableSensitiveDataLogging(false)
                    .EnableDetailedErrors(_configuration.EnableDetailedErrors);

                _dbContext = new FaceDbContext(optionsBuilder.Options);

                // Veritabanının oluşturulduğundan emin ol;
                await _dbContext.Database.EnsureCreatedAsync();

                _logger.LogDebug($"Database context initialized: {_configuration.DatabasePath}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize database context");
                throw;
            }
        }

        /// <summary>
        /// Migrations uygula;
        /// </summary>
        private async Task ApplyMigrationsAsync()
        {
            try
            {
                if (_configuration.ApplyMigrations)
                {
                    var pendingMigrations = await _dbContext.Database.GetPendingMigrationsAsync();
                    if (pendingMigrations.Any())
                    {
                        _logger.LogDebug($"Applying {pendingMigrations.Count()} pending migrations");
                        await _dbContext.Database.MigrateAsync();
                        _logger.LogInformation("Database migrations applied successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to apply migrations");
                throw;
            }
        }

        /// <summary>
        /// Servisleri başlat;
        /// </summary>
        private async Task InitializeServicesAsync()
        {
            try
            {
                // Search engine'i başlat;
                await _searchEngine.InitializeAsync(_dbContext, _configuration.SearchConfig);

                // Index manager'ı başlat;
                await _indexManager.InitializeAsync(_dbContext, _configuration.IndexConfig);

                // Encryption service'i başlat;
                await _encryptionService.InitializeAsync(_configuration.EncryptionConfig);

                // Backup service'i başlat;
                await _backupService.InitializeAsync(_configuration.BackupConfig);

                _logger.LogDebug("Database services initialized");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize services");
                throw;
            }
        }

        /// <summary>
        /// İstatistikleri güncelle;
        /// </summary>
        private async Task UpdateStatisticsAsync()
        {
            try
            {
                TotalPersons = await _dbContext.Persons.CountAsync();
                TotalFaces = await _dbContext.Faces.CountAsync();
                DatabaseSizeMB = await GetDatabaseFileSizeAsync();
                CacheSizeMB = CalculateCacheSizeMB();

                _logger.LogDebug($"Statistics updated: {TotalPersons} persons, {TotalFaces} faces");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update statistics");
            }
        }

        /// <summary>
        /// Cache istatistiklerini güncelle;
        /// </summary>
        private void UpdateCacheStatistics()
        {
            CacheStatistics = new CacheStatistics;
            {
                FaceCacheCount = _faceCache.Count,
                PersonCacheCount = _personCache.Count,
                FaceCacheHits = _faceCache.Values.Sum(c => c.AccessCount),
                PersonCacheHits = _personCache.Values.Sum(c => c.AccessCount),
                CacheSizeMB = CalculateCacheSizeMB(),
                LastUpdated = DateTime.UtcNow;
            };
        }

        /// <summary>
        /// Cache boyutunu hesapla (MB)
        /// </summary>
        private double CalculateCacheSizeMB()
        {
            long totalBytes = 0;

            // Face cache boyutu;
            foreach (var entry in _faceCache.Values)
            {
                totalBytes += EstimateObjectSize(entry);
            }

            // Person cache boyutu;
            foreach (var entry in _personCache.Values)
            {
                totalBytes += EstimateObjectSize(entry);
            }

            return totalBytes / (1024.0 * 1024.0);
        }

        /// <summary>
        /// Nesne boyutunu tahmin et;
        /// </summary>
        private long EstimateObjectSize(object obj)
        {
            if (obj == null) return 0;

            // Basit tahmin - gerçek uygulamada daha doğru hesaplama;
            return 1024; // 1KB tahmini;
        }

        /// <summary>
        /// Kişi entity'si getir;
        /// </summary>
        private async Task<PersonEntity> GetPersonEntityAsync(Guid personId)
        {
            return await _dbContext.Persons;
                .FirstOrDefaultAsync(p => p.Id == personId);
        }

        /// <summary>
        /// Yüz entity'si getir;
        /// </summary>
        private async Task<FaceEntity> GetFaceEntityAsync(Guid faceId)
        {
            return await _dbContext.Faces;
                .FirstOrDefaultAsync(f => f.Id == faceId);
        }

        /// <summary>
        /// Yüz özelliklerini çıkar;
        /// </summary>
        private async Task<byte[]> ExtractFaceFeaturesAsync(FaceData faceData)
        {
            // Gerçek uygulamada yüz tanıma algoritması kullan;
            // Bu örnekte basit hash kullanıyoruz;

            using var sha256 = SHA256.Create();
            return await Task.Run(() => sha256.ComputeHash(faceData.ImageData));
        }

        /// <summary>
        /// Görüntü hash'ini hesapla;
        /// </summary>
        private string ComputeImageHash(byte[] imageData)
        {
            using var md5 = MD5.Create();
            var hash = md5.ComputeHash(imageData);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Görüntü kalitesini hesapla;
        /// </summary>
        private double CalculateImageQuality(byte[] imageData)
        {
            // Basit kalite hesaplama - gerçek uygulamada daha gelişmiş algoritma;
            try
            {
                using var ms = new MemoryStream(imageData);
                using var image = Image.FromStream(ms);

                // Çözünürlük kalitesi;
                var resolutionQuality = Math.Min(1.0, (image.Width * image.Height) / (1920.0 * 1080.0));

                // Parazit/ blur tahmini (basit)
                var noiseQuality = 0.8; // Varsayılan;

                return (resolutionQuality + noiseQuality) / 2.0;
            }
            catch
            {
                return 0.5; // Varsayılan kalite;
            }
        }

        /// <summary>
        /// Primary yüz olarak ayarlanmalı mı kontrol et;
        /// </summary>
        private async Task<bool> ShouldSetAsPrimaryAsync(Guid personId)
        {
            var faceCount = await _dbContext.Faces;
                .CountAsync(f => f.PersonId == personId && f.IsActive);

            return faceCount == 0; // İlk yüz ise primary;
        }

        /// <summary>
        /// Diğer yüzlerin primary flag'ini kaldır;
        /// </summary>
        private async Task RemovePrimaryFlagFromOtherFacesAsync(Guid personId)
        {
            var primaryFaces = await _dbContext.Faces;
                .Where(f => f.PersonId == personId && f.IsPrimary && f.IsActive)
                .ToListAsync();

            foreach (var face in primaryFaces)
            {
                face.IsPrimary = false;
            }
        }

        /// <summary>
        /// Yeni primary yüz ayarla;
        /// </summary>
        private async Task SetNewPrimaryFaceAsync(Guid personId)
        {
            var bestFace = await _dbContext.Faces;
                .Where(f => f.PersonId == personId && f.IsActive)
                .OrderByDescending(f => f.ImageQuality)
                .ThenByDescending(f => f.CreatedAt)
                .FirstOrDefaultAsync();

            if (bestFace != null)
            {
                bestFace.IsPrimary = true;
                _dbContext.Faces.Update(bestFace);
            }
        }

        /// <summary>
        /// PersonRecord oluştur;
        /// </summary>
        private PersonRecord CreatePersonRecord(PersonEntity entity)
        {
            return new PersonRecord;
            {
                Id = entity.Id,
                Name = entity.Name,
                Surname = entity.Surname,
                Email = entity.Email,
                Phone = entity.Phone,
                DateOfBirth = entity.DateOfBirth,
                Gender = entity.Gender,
                NationalId = entity.NationalId,
                FaceCount = entity.FaceCount,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt,
                IsActive = entity.IsActive,
                Metadata = DeserializeMetadata(entity.Metadata)
            };
        }

        /// <summary>
        /// FaceRecord oluştur;
        /// </summary>
        private FaceRecord CreateFaceRecord(FaceEntity faceEntity, PersonEntity personEntity)
        {
            return new FaceRecord;
            {
                Id = faceEntity.Id,
                PersonId = faceEntity.PersonId,
                PersonName = personEntity?.Name,
                PersonSurname = personEntity?.Surname,
                ImageHash = faceEntity.ImageHash,
                FeatureVector = faceEntity.FeatureVector,
                ImageQuality = faceEntity.ImageQuality,
                Pose = faceEntity.Pose,
                Expression = faceEntity.Expression,
                Lighting = faceEntity.Lighting,
                CreatedAt = faceEntity.CreatedAt,
                IsActive = faceEntity.IsActive,
                IsPrimary = faceEntity.IsPrimary,
                Metadata = DeserializeMetadata(faceEntity.Metadata)
            };
        }

        /// <summary>
        /// Metadata serialize et;
        /// </summary>
        private string SerializeMetadata(Dictionary<string, object> metadata)
        {
            if (metadata == null || !metadata.Any())
                return null;

            return JsonConvert.SerializeObject(metadata, Formatting.None);
        }

        /// <summary>
        /// Metadata deserialize et;
        /// </summary>
        private Dictionary<string, object> DeserializeMetadata(string metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson))
                return new Dictionary<string, object>();

            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, object>>(metadataJson);
            }
            catch
            {
                return new Dictionary<string, object>();
            }
        }

        /// <summary>
        /// Face cache anahtarı oluştur;
        /// </summary>
        private string GenerateFaceCacheKey(Guid faceId)
        {
            return $"FACE_{faceId}";
        }

        /// <summary>
        /// Cache'leri temizle;
        /// </summary>
        private void ClearCaches()
        {
            _faceCache.Clear();
            _personCache.Clear();
            UpdateCacheStatistics();

            _logger.LogDebug("Caches cleared");
        }

        /// <summary>
        /// Kişi yüz cache'lerini temizle;
        /// </summary>
        private void ClearPersonFaceCache(Guid personId)
        {
            var keysToRemove = _faceCache.Keys;
                .Where(k => _faceCache[k].Face.PersonId == personId)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _faceCache.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Veritabanı dosya boyutunu getir (MB)
        /// </summary>
        private async Task<double> GetDatabaseFileSizeAsync()
        {
            try
            {
                if (File.Exists(_configuration.DatabasePath))
                {
                    var fileInfo = new FileInfo(_configuration.DatabasePath);
                    return fileInfo.Length / (1024.0 * 1024.0);
                }
                return 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Varsayılan yedekleme yolu;
        /// </summary>
        private string GetDefaultBackupPath()
        {
            var backupDir = Path.Combine(
                Path.GetDirectoryName(_configuration.DatabasePath),
                "Backups",
                DateTime.UtcNow.ToString("yyyy-MM-dd"));

            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            return Path.Combine(backupDir, $"face_db_backup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak");
        }

        /// <summary>
        /// Veritabanı dosyalarını listele;
        /// </summary>
        private List<string> GetDatabaseFiles()
        {
            var files = new List<string>();
            var databaseDir = Path.GetDirectoryName(_configuration.DatabasePath);
            var databaseName = Path.GetFileNameWithoutExtension(_configuration.DatabasePath);

            if (Directory.Exists(databaseDir))
            {
                var pattern = $"{databaseName}*";
                files.AddRange(Directory.GetFiles(databaseDir, pattern));
            }

            return files;
        }

        /// <summary>
        /// Log dosyalarını sil;
        /// </summary>
        private void DeleteLogFiles()
        {
            try
            {
                var logDir = Path.Combine(
                    Path.GetDirectoryName(_configuration.DatabasePath),
                    "Logs");

                if (Directory.Exists(logDir))
                {
                    Directory.Delete(logDir, true);
                    _logger.LogDebug($"Log directory deleted: {logDir}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete log files");
            }
        }

        /// <summary>
        /// Cache dosyalarını sil;
        /// </summary>
        private void DeleteCacheFiles()
        {
            try
            {
                var cacheDir = Path.Combine(
                    Path.GetDirectoryName(_configuration.DatabasePath),
                    "Cache");

                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, true);
                    _logger.LogDebug($"Cache directory deleted: {cacheDir}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete cache files");
            }
        }

        /// <summary>
        /// Bakım görevini başlat;
        /// </summary>
        private void StartMaintenanceTask()
        {
            _ = Task.Run(async () =>
            {
                while (!_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    try
                    {
                        // Her 6 saatte bir bakım yap;
                        await Task.Delay(TimeSpan.FromHours(6), _cancellationTokenSource.Token);

                        if (_isMaintenanceRunning || !_isInitialized)
                            continue;

                        _isMaintenanceRunning = true;

                        // Otomatik yedekleme;
                        if (_configuration.AutoBackupEnabled)
                        {
                            await PerformAutoBackupAsync();
                        }

                        // Cache temizleme;
                        await PerformCacheCleanupAsync();

                        // İstatistik güncelleme;
                        await UpdateStatisticsAsync();

                        _isMaintenanceRunning = false;
                    }
                    catch (OperationCanceledException)
                    {
                        // Görev iptal edildi;
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Maintenance task failed");
                    }
                }
            }, _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Otomatik yedekleme yap;
        /// </summary>
        private async Task PerformAutoBackupAsync()
        {
            try
            {
                var timeSinceLastBackup = DateTime.UtcNow - _lastBackupTime;
                if (timeSinceLastBackup >= _configuration.AutoBackupInterval)
                {
                    _logger.LogDebug("Performing auto backup");

                    await BackupDatabaseAsync(
                        backupPath: null,
                        type: BackupType.Incremental);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto backup failed");
            }
        }

        /// <summary>
        /// Cache temizliği yap;
        /// </summary>
        private async Task PerformCacheCleanupAsync()
        {
            try
            {
                var cleanupTime = DateTime.UtcNow - TimeSpan.FromHours(_configuration.CacheRetentionHours);

                // Eski face cache'leri temizle;
                var oldFaceKeys = _faceCache;
                    .Where(kvp => kvp.Value.LastAccess < cleanupTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldFaceKeys)
                {
                    _faceCache.TryRemove(key, out _);
                }

                // Eski person cache'leri temizle;
                var oldPersonKeys = _personCache;
                    .Where(kvp => kvp.Value.LastAccess < cleanupTime)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var key in oldPersonKeys)
                {
                    _personCache.TryRemove(key, out _);
                }

                if (oldFaceKeys.Count > 0 || oldPersonKeys.Count > 0)
                {
                    _logger.LogDebug($"Cache cleanup: {oldFaceKeys.Count} faces, {oldPersonKeys.Count} persons removed");
                    UpdateCacheStatistics();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cache cleanup failed");
            }
        }

        /// <summary>
        /// Başlatıldığını doğrula;
        /// </summary>
        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("Face database is not initialized. Call InitializeAsync first.");
        }

        #endregion;

        #region Event Triggers;

        private void OnPersonAdded(PersonRecord person)
        {
            PersonAdded?.Invoke(this, new PersonAddedEventArgs(person));
            _eventBus.Publish(new PersonAddedEvent(person));
        }

        private void OnFaceAdded(FaceRecord face)
        {
            FaceAdded?.Invoke(this, new FaceAddedEventArgs(face));
            _eventBus.Publish(new FaceAddedEvent(face));
        }

        private void OnPersonDeleted(Guid personId, bool softDelete)
        {
            PersonDeleted?.Invoke(this, new PersonDeletedEventArgs(personId, softDelete));
            _eventBus.Publish(new PersonDeletedEvent(personId, softDelete));
        }

        private void OnFaceDeleted(Guid faceId, Guid personId, bool softDelete)
        {
            FaceDeleted?.Invoke(this, new FaceDeletedEventArgs(faceId, personId, softDelete));
            _eventBus.Publish(new FaceDeletedEvent(faceId, personId, softDelete));
        }

        private void OnBackupCompleted(BackupResult backupResult)
        {
            BackupCompleted?.Invoke(this, new DatabaseBackupCompletedEventArgs(backupResult));
            _eventBus.Publish(new DatabaseBackupCompletedEvent(backupResult));
        }

        private void OnDatabaseOptimized(OptimizationResult optimizationResult)
        {
            DatabaseOptimized?.Invoke(this, new DatabaseOptimizedEventArgs(optimizationResult));
            _eventBus.Publish(new DatabaseOptimizedEvent(optimizationResult));
        }

        private void OnStateChanged()
        {
            StateChanged?.Invoke(this, new DatabaseStateChangedEventArgs(State));
            _eventBus.Publish(new DatabaseStateChangedEvent(State));
        }

        private void OnDatabaseCleaned(CleanupResult cleanupResult)
        {
            DatabaseCleaned?.Invoke(this, new DatabaseCleanedEventArgs(cleanupResult));
            _eventBus.Publish(new DatabaseCleanedEvent(cleanupResult));
        }

        private void OnConfigurationChanged()
        {
            // Konfigürasyon değiştiğinde güncellemeler yap;
            if (_isInitialized)
            {
                _logger.LogInformation("Database configuration changed");
                // Gerekli güncellemeleri yap;
            }
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
                    _cancellationTokenSource?.Cancel();
                    _cancellationTokenSource?.Dispose();

                    _dbContext?.Dispose();
                    _dbSemaphore?.Dispose();

                    // Servisleri temizle;
                    _searchEngine?.Dispose();
                    _indexManager?.Dispose();
                    _encryptionService?.Dispose();
                    _backupService?.Dispose();

                    // Cache'leri temizle;
                    ClearCaches();

                    // Event subscription'ları temizle;
                    PersonAdded = null;
                    FaceAdded = null;
                    PersonDeleted = null;
                    FaceDeleted = null;
                    BackupCompleted = null;
                    DatabaseOptimized = null;
                    StateChanged = null;
                    DatabaseCleaned = null;
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

        #region Supporting Classes and Enums;

        /// <summary>
        /// Veritabanı durumları;
        /// </summary>
        public enum DatabaseState;
        {
            Closed,
            Opening,
            Open,
            BackupInProgress,
            RestoreInProgress,
            OptimizationInProgress,
            CleanupInProgress,
            Error;
        }

        /// <summary>
        /// Yedekleme tipleri;
        /// </summary>
        public enum BackupType;
        {
            Full,
            Incremental,
            Differential;
        }

        /// <summary>
        /// Arama modları;
        /// </summary>
        public enum SearchMode;
        {
            Exact,
            Similar,
            Recognition,
            Advanced;
        }

        /// <summary>
        /// Yüz pozları;
        /// </summary>
        public enum FacePose;
        {
            Front,
            Left,
            Right,
            Up,
            Down,
            Unknown;
        }

        /// <summary>
        /// Yüz ifadeleri;
        /// </summary>
        public enum FaceExpression;
        {
            Neutral,
            Happy,
            Sad,
            Angry,
            Surprised,
            Unknown;
        }

        /// <summary>
        /// Aydınlatma koşulları;
        /// </summary>
        public enum LightingCondition;
        {
            Good,
            Low,
            Backlit,
            Harsh,
            Unknown;
        }

        /// <summary>
        /// Cinsiyet;
        /// </summary>
        public enum Gender;
        {
            Unknown,
            Male,
            Female,
            Other;
        }

        #endregion;
    }

    #region Data Classes;

    /// <summary>
    /// Kişi verisi;
    /// </summary>
    public class PersonData;
    {
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public Gender? Gender { get; set; }
        public string NationalId { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Yüz verisi;
    /// </summary>
    public class FaceData;
    {
        public byte[] ImageData { get; set; }
        public double QualityScore { get; set; }
        public FacePose Pose { get; set; } = FacePose.Unknown;
        public FaceExpression Expression { get; set; } = FaceExpression.Unknown;
        public LightingCondition Lighting { get; set; } = LightingCondition.Unknown;
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Kişi kaydı;
    /// </summary>
    public class PersonRecord : PersonData;
    {
        public Guid Id { get; set; }
        public int FaceCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsActive { get; set; }

        public PersonEntity ToPersonEntity()
        {
            return new PersonEntity;
            {
                Id = Id,
                Name = Name,
                Surname = Surname,
                Email = Email,
                Phone = Phone,
                DateOfBirth = DateOfBirth,
                Gender = Gender ?? Gender.Unknown,
                NationalId = NationalId,
                FaceCount = FaceCount,
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                IsActive = IsActive;
            };
        }
    }

    /// <summary>
    /// Yüz kaydı;
    /// </summary>
    public class FaceRecord : FaceData;
    {
        public Guid Id { get; set; }
        public Guid PersonId { get; set; }
        public string PersonName { get; set; }
        public string PersonSurname { get; set; }
        public string ImageHash { get; set; }
        public byte[] FeatureVector { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public bool IsPrimary { get; set; }
    }

    /// <summary>
    /// Kişi entity'si;
    /// </summary>
    public class PersonEntity;
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public Gender Gender { get; set; }
        public string NationalId { get; set; }
        public int FaceCount { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public bool IsActive { get; set; }
        public string Metadata { get; set; }

        // Şifreli alanlar;
        public string EncryptedName { get; set; }
        public string EncryptedSurname { get; set; }
        public string EncryptedEmail { get; set; }
        public string EncryptedPhone { get; set; }
        public string EncryptedNationalId { get; set; }
    }

    /// <summary>
    /// Yüz entity'si;
    /// </summary>
    public class FaceEntity;
    {
        public Guid Id { get; set; }
        public Guid PersonId { get; set; }
        public string ImageHash { get; set; }
        public byte[] FeatureVector { get; set; }
        public double ImageQuality { get; set; }
        public FacePose Pose { get; set; }
        public FaceExpression Expression { get; set; }
        public LightingCondition Lighting { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsActive { get; set; }
        public bool IsPrimary { get; set; }
        public string Metadata { get; set; }

        // Şifreli alanlar;
        public byte[] EncryptedFeatureVector { get; set; }
    }

    /// <summary>
    /// Batch yüz sonucu;
    /// </summary>
    public class BatchFaceResult;
    {
        public Guid PersonId { get; set; }
        public int SuccessfulCount { get; set; }
        public int FailedCount { get; set; }
        public List<FaceRecord> FaceRecords { get; set; } = new List<FaceRecord>();
        public List<FailedFace> FailedFaces { get; set; } = new List<FailedFace>();
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Başarısız yüz;
    /// </summary>
    public class FailedFace;
    {
        public FaceData FaceData { get; set; }
        public string Error { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Yüz arama kriterleri;
    /// </summary>
    public class FaceSearchCriteria;
    {
        public byte[] FeatureVector { get; set; }
        public double? MinConfidence { get; set; }
        public double? MaxDistance { get; set; }
        public int? MaxResults { get; set; }
        public SearchMode SearchMode { get; set; }
        public Guid? PersonId { get; set; }
        public bool? IsActive { get; set; }
        public DateTime? CreatedAfter { get; set; }
        public DateTime? CreatedBefore { get; set; }
        public Dictionary<string, object> AdditionalCriteria { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Yüz arama sonucu;
    /// </summary>
    public class FaceSearchResult;
    {
        public FaceRecord FaceRecord { get; set; }
        public PersonRecord PersonRecord { get; set; }
        public double MatchScore { get; set; }
        public double Similarity { get; set; }
        public double Distance { get; set; }
        public List<string> MatchedFeatures { get; set; } = new List<string>();
    }

    /// <summary>
    /// Yüz tanıma sonucu;
    /// </summary>
    public class FaceRecognitionResult;
    {
        public byte[] QueryFeatures { get; set; }
        public List<RecognitionCandidate> Candidates { get; set; }
        public RecognitionCandidate BestMatch { get; set; }
        public bool IsRecognized { get; set; }
        public DateTime Timestamp { get; set; }
    }

    /// <summary>
    /// Tanıma adayı;
    /// </summary>
    public class RecognitionCandidate;
    {
        public PersonRecord PersonRecord { get; set; }
        public FaceRecord FaceRecord { get; set; }
        public double Confidence { get; set; }
        public double Similarity { get; set; }
        public double Distance { get; set; }
    }

    /// <summary>
    /// Kişi güncelleme verisi;
    /// </summary>
    public class PersonUpdateData;
    {
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public DateTime? DateOfBirth { get; set; }
        public Gender? Gender { get; set; }
        public bool? IsActive { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Yüz güncelleme verisi;
    /// </summary>
    public class FaceUpdateData;
    {
        public bool? IsPrimary { get; set; }
        public bool? IsActive { get; set; }
        public Dictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Yedekleme sonucu;
    /// </summary>
    public class BackupResult;
    {
        public string BackupPath { get; set; }
        public BackupType BackupType { get; set; }
        public double BackupSizeMB { get; set; }
        public DateTime BackupTime { get; set; }
        public bool IsEncrypted { get; set; }
        public bool IsCompressed { get; set; }
        public string Checksum { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Geri yükleme sonucu;
    /// </summary>
    public class RestoreResult;
    {
        public string BackupPath { get; set; }
        public DateTime RestoreTime { get; set; }
        public int RestoredItemCount { get; set; }
        public bool Success { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Optimizasyon sonucu;
    /// </summary>
    public class OptimizationResult;
    {
        public DateTime OptimizationTime { get; set; }
        public double SpaceFreedMB { get; set; }
        public int IndexesRebuilt { get; set; }
        public TimeSpan Duration { get; set; }
        public Dictionary<string, object> OptimizationMetrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// Temizlik kriterleri;
    /// </summary>
    public class CleanupCriteria;
    {
        public int? MaxAgeDays { get; set; } = 365;
        public bool RemoveInactive { get; set; } = true;
        public bool RemoveLowQuality { get; set; } = true;
        public double? MinQualityScore { get; set; } = 0.3;
        public bool RemoveDuplicates { get; set; } = true;
        public double? DuplicateThreshold { get; set; } = 0.95;

        public static CleanupCriteria Default => new CleanupCriteria();
    }

    /// <summary>
    /// Temizlik sonucu;
    /// </summary>
    public class CleanupResult;
    {
        public DateTime CleanupTime { get; set; }
        public int RemovedPersons { get; set; }
        public int RemovedFaces { get; set; }
        public int RemovedItems => RemovedPersons + RemovedFaces;
        public double SpaceFreedMB { get; set; }
        public TimeSpan Duration { get; set; }
    }

    /// <summary>
    /// Veritabanı istatistikleri;
    /// </summary>
    public class DatabaseStatistics;
    {
        public DateTime Timestamp { get; set; }
        public int TotalPersons { get; set; }
        public int TotalFaces { get; set; }
        public int ActivePersons { get; set; }
        public int InactivePersons { get; set; }
        public int ActiveFaces { get; set; }
        public int InactiveFaces { get; set; }
        public int PrimaryFaces { get; set; }
        public double AverageFacesPerPerson { get; set; }
        public double DatabaseSizeMB { get; set; }
        public double DatabaseFileSizeMB { get; set; }
        public double IndexSizeMB { get; set; }
        public double CacheSizeMB { get; set; }
        public DateTime LastBackupTime { get; set; }
        public CacheStatistics CacheStatistics { get; set; }
        public DatabaseConfiguration Configuration { get; set; }
    }

    /// <summary>
    /// Cache istatistikleri;
    /// </summary>
    public class CacheStatistics;
    {
        public int FaceCacheCount { get; set; }
        public int PersonCacheCount { get; set; }
        public long FaceCacheHits { get; set; }
        public long PersonCacheHits { get; set; }
        public double CacheSizeMB { get; set; }
        public DateTime LastUpdated { get; set; }
    }

    /// <summary>
    /// Cache girişi;
    /// </summary>
    public class FaceCacheEntry
    {
        public FaceEntity Face { get; set; }
        public byte[] FeatureVector { get; set; }
        public DateTime LastAccess { get; set; }
        public long AccessCount { get; set; }
    }

    /// <summary>
    /// Kişi cache girişi;
    /// </summary>
    public class PersonCacheEntry
    {
        public PersonEntity Person { get; set; }
        public DateTime LastAccess { get; set; }
        public long AccessCount { get; set; }
    }

    /// <summary>
    /// Veritabanı konfigürasyonu;
    /// </summary>
    public class DatabaseConfiguration;
    {
        public string DatabasePath { get; set; } = "FaceDatabase.db";
        public bool ApplyMigrations { get; set; } = true;
        public bool EnableDetailedErrors { get; set; } = false;
        public bool AutoBackupEnabled { get; set; } = true;
        public TimeSpan AutoBackupInterval { get; set; } = TimeSpan.FromDays(1);
        public int CacheRetentionHours { get; set; } = 24;
        public int MaxCacheSizeMB { get; set; } = 100;
        public SearchConfiguration SearchConfig { get; set; } = new SearchConfiguration();
        public IndexConfiguration IndexConfig { get; set; } = new IndexConfiguration();
        public EncryptionConfiguration EncryptionConfig { get; set; } = new EncryptionConfiguration();
        public BackupConfiguration BackupConfig { get; set; } = new BackupConfiguration();

        public static DatabaseConfiguration Default => new DatabaseConfiguration();
    }

    /// <summary>
    /// Arama konfigürasyonu;
    /// </summary>
    public class SearchConfiguration;
    {
        public double DefaultSimilarityThreshold { get; set; } = 0.8;
        public int DefaultMaxResults { get; set; } = 10;
        public bool UseAdvancedIndexing { get; set; } = true;
        public TimeSpan SearchTimeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Index konfigürasyonu;
    /// </summary>
    public class IndexConfiguration;
    {
        public bool EnableFaceIndex { get; set; } = true;
        public bool EnablePersonIndex { get; set; } = true;
        public bool EnableFeatureIndex { get; set; } = true;
        public int IndexRebuildIntervalHours { get; set; } = 24;
    }

    /// <summary>
    /// Şifreleme konfigürasyonu;
    /// </summary>
    public class EncryptionConfiguration;
    {
        public bool EncryptSensitiveData { get; set; } = true;
        public string EncryptionAlgorithm { get; set; } = "AES-256-GCM";
        public bool EncryptFeatureVectors { get; set; } = true;
        public bool EncryptMetadata { get; set; } = false;
    }

    /// <summary>
    /// Yedekleme konfigürasyonu;
    /// </summary>
    public class BackupConfiguration;
    {
        public bool CompressBackups { get; set; } = true;
        public bool EncryptBackups { get; set; } = true;
        public int MaxBackupCount { get; set; } = 10;
        public string BackupEncryptionKey { get; set; }
    }

    /// <summary>
    /// Veritabanı metriği;
    /// </summary>
    public class DatabaseMetric;
    {
        public string Name { get; set; }
        public double Value { get; set; }
        public DateTime Timestamp { get; set; }
        public string Unit { get; set; }
    }

    #endregion;

    #region Event Args Classes;

    public class PersonAddedEventArgs : EventArgs;
    {
        public PersonRecord Person { get; }

        public PersonAddedEventArgs(PersonRecord person)
        {
            Person = person ?? throw new ArgumentNullException(nameof(person));
        }
    }

    public class FaceAddedEventArgs : EventArgs;
    {
        public FaceRecord Face { get; }

        public FaceAddedEventArgs(FaceRecord face)
        {
            Face = face ?? throw new ArgumentNullException(nameof(face));
        }
    }

    public class PersonDeletedEventArgs : EventArgs;
    {
        public Guid PersonId { get; }
        public bool SoftDelete { get; }

        public PersonDeletedEventArgs(Guid personId, bool softDelete)
        {
            PersonId = personId;
            SoftDelete = softDelete;
        }
    }

    public class FaceDeletedEventArgs : EventArgs;
    {
        public Guid FaceId { get; }
        public Guid PersonId { get; }
        public bool SoftDelete { get; }

        public FaceDeletedEventArgs(Guid faceId, Guid personId, bool softDelete)
        {
            FaceId = faceId;
            PersonId = personId;
            SoftDelete = softDelete;
        }
    }

    public class DatabaseBackupCompletedEventArgs : EventArgs;
    {
        public BackupResult BackupResult { get; }

        public DatabaseBackupCompletedEventArgs(BackupResult backupResult)
        {
            BackupResult = backupResult ?? throw new ArgumentNullException(nameof(backupResult));
        }
    }

    public class DatabaseOptimizedEventArgs : EventArgs;
    {
        public OptimizationResult OptimizationResult { get; }

        public DatabaseOptimizedEventArgs(OptimizationResult optimizationResult)
        {
            OptimizationResult = optimizationResult ?? throw new ArgumentNullException(nameof(optimizationResult));
        }
    }

    public class DatabaseStateChangedEventArgs : EventArgs;
    {
        public DatabaseState State { get; }

        public DatabaseStateChangedEventArgs(DatabaseState state)
        {
            State = state;
        }
    }

    public class DatabaseCleanedEventArgs : EventArgs;
    {
        public CleanupResult CleanupResult { get; }

        public DatabaseCleanedEventArgs(CleanupResult cleanupResult)
        {
            CleanupResult = cleanupResult ?? throw new ArgumentNullException(nameof(cleanupResult));
        }
    }

    #endregion;

    #region Entity Framework Context;

    /// <summary>
    /// Face Database Context;
    /// </summary>
    public class FaceDbContext : DbContext;
    {
        public DbSet<PersonEntity> Persons { get; set; }
        public DbSet<FaceEntity> Faces { get; set; }

        public FaceDbContext(DbContextOptions<FaceDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Person entity configuration;
            modelBuilder.Entity<PersonEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.Name);
                entity.HasIndex(e => e.Surname);
                entity.HasIndex(e => e.Email).IsUnique();
                entity.HasIndex(e => e.Phone).IsUnique();
                entity.HasIndex(e => e.NationalId).IsUnique();
                entity.HasIndex(e => e.IsActive);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => e.UpdatedAt);

                entity.Property(e => e.Name).HasMaxLength(100);
                entity.Property(e => e.Surname).HasMaxLength(100);
                entity.Property(e => e.Email).HasMaxLength(200);
                entity.Property(e => e.Phone).HasMaxLength(20);
                entity.Property(e => e.NationalId).HasMaxLength(50);
                entity.Property(e => e.Gender).HasConversion<string>();
                entity.Property(e => e.Metadata).HasColumnType("TEXT");

                // Encrypted fields;
                entity.Property(e => e.EncryptedName).HasMaxLength(500);
                entity.Property(e => e.EncryptedSurname).HasMaxLength(500);
                entity.Property(e => e.EncryptedEmail).HasMaxLength(500);
                entity.Property(e => e.EncryptedPhone).HasMaxLength(500);
                entity.Property(e => e.EncryptedNationalId).HasMaxLength(500);
            });

            // Face entity configuration;
            modelBuilder.Entity<FaceEntity>(entity =>
            {
                entity.HasKey(e => e.Id);
                entity.HasIndex(e => e.PersonId);
                entity.HasIndex(e => e.ImageHash).IsUnique();
                entity.HasIndex(e => e.IsActive);
                entity.HasIndex(e => e.IsPrimary);
                entity.HasIndex(e => e.ImageQuality);
                entity.HasIndex(e => e.CreatedAt);
                entity.HasIndex(e => new { e.PersonId, e.IsActive, e.IsPrimary });

                entity.Property(e => e.ImageHash).HasMaxLength(64);
                entity.Property(e => e.FeatureVector).HasColumnType("BLOB");
                entity.Property(e => e.Pose).HasConversion<string>();
                entity.Property(e => e.Expression).HasConversion<string>();
                entity.Property(e => e.Lighting).HasConversion<string>();
                entity.Property(e => e.Metadata).HasColumnType("TEXT");

                // Encrypted field;
                entity.Property(e => e.EncryptedFeatureVector).HasColumnType("BLOB");

                // Foreign key relationship;
                entity.HasOne<PersonEntity>()
                    .WithMany()
                    .HasForeignKey(e => e.PersonId)
                    .OnDelete(DeleteBehavior.Cascade);
            });

            // Configure relationships;
            modelBuilder.Entity<PersonEntity>()
                .HasMany<FaceEntity>()
                .WithOne()
                .HasForeignKey(f => f.PersonId)
                .OnDelete(DeleteBehavior.Cascade);
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // Update timestamps;
            var entries = ChangeTracker.Entries()
                .Where(e => e.Entity is PersonEntity || e.Entity is FaceEntity)
                .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);

            foreach (var entry in entries)
            {
                if (entry.Entity is PersonEntity person)
                {
                    if (entry.State == EntityState.Added)
                    {
                        person.CreatedAt = DateTime.UtcNow;
                    }
                    person.UpdatedAt = DateTime.UtcNow;
                }
                else if (entry.Entity is FaceEntity face)
                {
                    if (entry.State == EntityState.Added)
                    {
                        face.CreatedAt = DateTime.UtcNow;
                    }
                }
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    #endregion;
}
