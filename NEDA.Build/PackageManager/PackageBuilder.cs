// NEDA.Build/PackageManager/PackageBuilder.cs;

using NEDA.Build.PackageManager.Configuration;
using NEDA.Build.PackageManager.Contracts;
using NEDA.Build.PackageManager.Dependencies;
using NEDA.Build.PackageManager.Exceptions;
using NEDA.Build.PackageManager.Formats;
using NEDA.Build.PackageManager.Metadata;
using NEDA.Build.PackageManager.Models;
using NEDA.Build.PackageManager.Signing;
using NEDA.Build.PackageManager.Validators;
using NEDA.Common.Extensions;
using NEDA.Common.Utilities;
using NEDA.Logging;
using NEDA.Monitoring;
using NEDA.SecurityModules.Encryption;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;

namespace NEDA.Build.PackageManager;
{
    /// <summary>
    /// Advanced package builder with support for multiple package formats,
    /// dependency resolution, digital signing, and comprehensive validation.
    /// </summary>
    public class PackageBuilder : IPackageBuilder, IDisposable;
    {
        #region Constants;

        private const int DEFAULT_BUFFER_SIZE = 81920;
        private const int MAX_PACKAGE_SIZE_MB = 1024;
        private const string MANIFEST_FILE_NAME = "package.manifest.json";
        private const string SIGNATURE_FILE_NAME = "package.signature.sig";
        private const string METADATA_FILE_NAME = "package.metadata.json";
        private const string DEPENDENCIES_FILE_NAME = "dependencies.json";
        private const string CONTENT_TYPES_FILE_NAME = "[Content_Types].xml";
        private const string NUSPEC_EXTENSION = ".nuspec";
        private const string NUPKG_EXTENSION = ".nupkg";
        private const string ZIP_EXTENSION = ".zip";

        #endregion;

        #region Private Fields;

        private readonly PackageBuilderConfiguration _configuration;
        private readonly ILogger _logger;
        private readonly IPackageValidator _validator;
        private readonly IPackageSigner _signer;
        private readonly IDependencyResolver _dependencyResolver;
        private readonly IMetadataGenerator _metadataGenerator;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly Dictionary<string, IPackageFormatHandler> _formatHandlers;
        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _buildSemaphore;
        private bool _disposed;
        private bool _isInitialized;
        private int _totalPackagesBuilt;
        private long _totalBuildTimeMs;

        #endregion;

        #region Properties;

        /// <summary>
        /// Gets the configuration used by this package builder.
        /// </summary>
        public PackageBuilderConfiguration Configuration => _configuration;

        /// <summary>
        /// Gets the number of packages built since initialization.
        /// </summary>
        public int TotalPackagesBuilt => _totalPackagesBuilt;

        /// <summary>
        /// Gets the average build time in milliseconds.
        /// </summary>
        public double AverageBuildTimeMs => _totalPackagesBuilt > 0 ?
            (double)_totalBuildTimeMs / _totalPackagesBuilt : 0;

        /// <summary>
        /// Gets whether the package builder is initialized.
        /// </summary>
        public bool IsInitialized => _isInitialized;

        /// <summary>
        /// Gets the supported package formats.
        /// </summary>
        public IReadOnlyList<string> SupportedFormats => _formatHandlers.Keys.ToList().AsReadOnly();

        #endregion;

        #region Events;

        /// <summary>
        /// Occurs when package building starts.
        /// </summary>
        public event EventHandler<PackageBuildStartedEventArgs> BuildStarted;

        /// <summary>
        /// Occurs when package building completes.
        /// </summary>
        public event EventHandler<PackageBuildCompletedEventArgs> BuildCompleted;

        /// <summary>
        /// Occurs when package building fails.
        /// </summary>
        public event EventHandler<PackageBuildFailedEventArgs> BuildFailed;

        /// <summary>
        /// Occurs when a package file is added.
        /// </summary>
        public event EventHandler<PackageFileAddedEventArgs> FileAdded;

        /// <summary>
        /// Occurs when a package is signed.
        /// </summary>
        public event EventHandler<PackageSignedEventArgs> PackageSigned;

        /// <summary>
        /// Occurs when dependencies are resolved.
        /// </summary>
        public event EventHandler<DependenciesResolvedEventArgs> DependenciesResolved;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Initializes a new instance of the PackageBuilder class with default configuration.
        /// </summary>
        public PackageBuilder()
            : this(new PackageBuilderConfiguration(), null, null, null, null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the PackageBuilder class with specified configuration.
        /// </summary>
        /// <param name="configuration">The package builder configuration.</param>
        /// <param name="logger">The logger instance (optional).</param>
        /// <param name="validator">The package validator (optional).</param>
        /// <param name="signer">The package signer (optional).</param>
        /// <param name="dependencyResolver">The dependency resolver (optional).</param>
        /// <param name="metadataGenerator">The metadata generator (optional).</param>
        public PackageBuilder(
            PackageBuilderConfiguration configuration,
            ILogger logger = null,
            IPackageValidator validator = null,
            IPackageSigner signer = null,
            IDependencyResolver dependencyResolver = null,
            IMetadataGenerator metadataGenerator = null)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? LogManager.GetLogger(typeof(PackageBuilder));
            _validator = validator ?? new PackageValidator();
            _signer = signer ?? new PackageSigner();
            _dependencyResolver = dependencyResolver ?? new DependencyResolver();
            _metadataGenerator = metadataGenerator ?? new MetadataGenerator();

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };

            _formatHandlers = new Dictionary<string, IPackageFormatHandler>();
            _buildSemaphore = new SemaphoreSlim(
                _configuration.MaxConcurrentBuilds,
                _configuration.MaxConcurrentBuilds);

            InitializeFormatHandlers();
        }

        #endregion;

        #region Public Methods - Initialization;

        /// <summary>
        /// Initializes the package builder asynchronously.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
                return;

            try
            {
                _logger.Info("Initializing PackageBuilder...");

                // Initialize working directory;
                CreateWorkingDirectory();

                // Initialize format handlers;
                await InitializeFormatHandlersAsync(cancellationToken);

                // Initialize signer;
                await _signer.InitializeAsync(cancellationToken);

                // Initialize dependency resolver;
                await _dependencyResolver.InitializeAsync(cancellationToken);

                _isInitialized = true;
                _logger.Info("PackageBuilder initialized successfully.");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize PackageBuilder: {ex.Message}", ex);
                throw new PackageBuilderInitializationException(
                    "Failed to initialize PackageBuilder", ex);
            }
        }

        /// <summary>
        /// Registers a custom package format handler.
        /// </summary>
        /// <param name="format">The package format.</param>
        /// <param name="handler">The format handler.</param>
        public void RegisterFormatHandler(string format, IPackageFormatHandler handler)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(format))
                throw new ArgumentException("Format cannot be null or empty", nameof(format));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            lock (_syncLock)
            {
                if (_formatHandlers.ContainsKey(format))
                {
                    throw new InvalidOperationException(
                        $"Format handler for '{format}' is already registered");
                }

                _formatHandlers[format] = handler;
                _logger.Info($"Registered format handler: {format}");
            }
        }

        /// <summary>
        /// Unregisters a package format handler.
        /// </summary>
        /// <param name="format">The package format.</param>
        /// <returns>True if the handler was unregistered; otherwise, false.</returns>
        public bool UnregisterFormatHandler(string format)
        {
            ValidateNotDisposed();

            if (string.IsNullOrWhiteSpace(format))
                return false;

            lock (_syncLock)
            {
                if (_formatHandlers.TryGetValue(format, out var handler))
                {
                    handler.Dispose();
                    _formatHandlers.Remove(format);
                    _logger.Info($"Unregistered format handler: {format}");
                    return true;
                }

                return false;
            }
        }

        #endregion;

        #region Public Methods - Package Building;

        /// <summary>
        /// Builds a package asynchronously from the specified source.
        /// </summary>
        /// <param name="request">The package build request.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The package build result.</returns>
        public async Task<PackageBuildResult> BuildPackageAsync(
            PackageBuildRequest request,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (request == null)
                throw new ArgumentNullException(nameof(request));

            _validator.ValidateBuildRequest(request);

            // Wait for semaphore slot;
            var semaphoreAcquired = await _buildSemaphore.WaitAsync(
                TimeSpan.FromSeconds(30),
                cancellationToken);

            if (!semaphoreAcquired)
            {
                throw new PackageBuildException(
                    "Could not acquire build slot within timeout");
            }

            var stopwatch = Stopwatch.StartNew();
            string packageId = $"{request.PackageId}-{request.Version}";
            string buildId = GenerateBuildId(request);

            try
            {
                // Fire build started event;
                OnBuildStarted(new PackageBuildStartedEventArgs;
                {
                    BuildId = buildId,
                    PackageId = request.PackageId,
                    PackageVersion = request.Version,
                    Format = request.Format,
                    StartTime = DateTime.UtcNow,
                    SourcePath = request.SourcePath;
                });

                _logger.Info($"Building package: {packageId} (Format: {request.Format})");

                // Validate source;
                await ValidateSourceAsync(request, cancellationToken);

                // Resolve dependencies;
                var dependencies = await ResolveDependenciesAsync(request, cancellationToken);

                // Generate metadata;
                var metadata = await GenerateMetadataAsync(request, dependencies, cancellationToken);

                // Create package structure;
                var packageStructure = await CreatePackageStructureAsync(
                    request, metadata, dependencies, cancellationToken);

                // Build package based on format;
                var packageFile = await BuildPackageByFormatAsync(
                    request, packageStructure, cancellationToken);

                // Validate package;
                await ValidatePackageAsync(packageFile, request, cancellationToken);

                // Sign package if configured;
                if (_configuration.SignPackages)
                {
                    packageFile = await SignPackageAsync(packageFile, request, cancellationToken);
                }

                // Calculate hash;
                var packageHash = await CalculatePackageHashAsync(packageFile, cancellationToken);

                // Update statistics;
                stopwatch.Stop();
                Interlocked.Increment(ref _totalPackagesBuilt);
                Interlocked.Add(ref _totalBuildTimeMs, stopwatch.ElapsedMilliseconds);

                // Create result;
                var result = new PackageBuildResult;
                {
                    BuildId = buildId,
                    PackageId = request.PackageId,
                    PackageVersion = request.Version,
                    PackageFile = packageFile,
                    PackageHash = packageHash,
                    Format = request.Format,
                    Dependencies = dependencies,
                    Metadata = metadata,
                    BuildTime = stopwatch.Elapsed,
                    BuildSize = new FileInfo(packageFile).Length,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                };

                // Fire build completed event;
                OnBuildCompleted(new PackageBuildCompletedEventArgs;
                {
                    BuildId = buildId,
                    PackageId = request.PackageId,
                    PackageVersion = request.Version,
                    PackageFile = packageFile,
                    PackageHash = packageHash,
                    BuildTime = stopwatch.Elapsed,
                    BuildSize = result.BuildSize,
                    DependenciesCount = dependencies?.Count ?? 0;
                });

                _logger.Info($"Package built successfully: {packageId} ({stopwatch.Elapsed.TotalSeconds:F2}s)");

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();

                // Fire build failed event;
                OnBuildFailed(new PackageBuildFailedEventArgs;
                {
                    BuildId = buildId,
                    PackageId = request.PackageId,
                    PackageVersion = request.Version,
                    Error = ex.Message,
                    BuildTime = stopwatch.Elapsed,
                    Exception = ex;
                });

                _logger.Error($"Package build failed: {packageId} - {ex.Message}", ex);
                throw new PackageBuildException($"Failed to build package {packageId}", ex);
            }
            finally
            {
                // Release semaphore;
                _buildSemaphore.Release();
            }
        }

        /// <summary>
        /// Builds multiple packages in parallel.
        /// </summary>
        /// <param name="requests">The package build requests.</param>
        /// <param name="maxParallel">Maximum parallel builds.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The package build results.</returns>
        public async Task<IReadOnlyList<PackageBuildResult>> BuildPackagesAsync(
            IEnumerable<PackageBuildRequest> requests,
            int? maxParallel = null,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (requests == null)
                throw new ArgumentNullException(nameof(requests));

            var requestList = requests.ToList();
            if (requestList.Count == 0)
                return new List<PackageBuildResult>().AsReadOnly();

            var maxConcurrent = maxParallel ?? _configuration.MaxConcurrentBuilds;
            var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            var tasks = new List<Task<PackageBuildResult>>();
            var results = new List<PackageBuildResult>();

            _logger.Info($"Building {requestList.Count} packages (max parallel: {maxConcurrent})");

            foreach (var request in requestList)
            {
                await semaphore.WaitAsync(cancellationToken);

                var task = Task.Run(async () =>
                {
                    try
                    {
                        return await BuildPackageAsync(request, cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, cancellationToken);

                tasks.Add(task);
            }

            // Wait for all tasks to complete;
            var completedTasks = await Task.WhenAll(tasks);
            results.AddRange(completedTasks.Where(r => r != null));

            _logger.Info($"Completed building {results.Count} packages");

            return results.AsReadOnly();
        }

        /// <summary>
        /// Creates a package from an existing directory.
        /// </summary>
        /// <param name="sourceDirectory">The source directory.</param>
        /// <param name="outputFile">The output package file.</param>
        /// <param name="format">The package format.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The package build result.</returns>
        public async Task<PackageBuildResult> CreatePackageFromDirectoryAsync(
            string sourceDirectory,
            string outputFile,
            PackageFormat format = PackageFormat.Zip,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sourceDirectory))
                throw new ArgumentException("Source directory cannot be null or empty", nameof(sourceDirectory));

            if (string.IsNullOrWhiteSpace(outputFile))
                throw new ArgumentException("Output file cannot be null or empty", nameof(outputFile));

            if (!Directory.Exists(sourceDirectory))
                throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");

            var request = new PackageBuildRequest;
            {
                PackageId = Path.GetFileNameWithoutExtension(outputFile),
                Version = "1.0.0",
                Format = format,
                SourcePath = sourceDirectory,
                OutputPath = Path.GetDirectoryName(outputFile),
                IncludeSource = true,
                IncludeSymbols = false,
                CompressionLevel = CompressionLevel.Optimal;
            };

            return await BuildPackageAsync(request, cancellationToken);
        }

        /// <summary>
        /// Extracts a package to the specified directory.
        /// </summary>
        /// <param name="packageFile">The package file.</param>
        /// <param name="targetDirectory">The target directory.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The extraction result.</returns>
        public async Task<PackageExtractionResult> ExtractPackageAsync(
            string packageFile,
            string targetDirectory,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(packageFile))
                throw new ArgumentException("Package file cannot be null or empty", nameof(packageFile));

            if (string.IsNullOrWhiteSpace(targetDirectory))
                throw new ArgumentException("Target directory cannot be null or empty", nameof(targetDirectory));

            if (!File.Exists(packageFile))
                throw new FileNotFoundException($"Package file not found: {packageFile}");

            var format = DetectPackageFormat(packageFile);
            if (!_formatHandlers.TryGetValue(format.ToString(), out var handler))
            {
                throw new PackageFormatException($"Unsupported package format: {format}");
            }

            var stopwatch = Stopwatch.StartNew();

            try
            {
                _logger.Info($"Extracting package: {packageFile} to {targetDirectory}");

                // Create target directory;
                Directory.CreateDirectory(targetDirectory);

                // Extract package;
                var extractionResult = await handler.ExtractPackageAsync(
                    packageFile,
                    targetDirectory,
                    cancellationToken);

                // Load manifest;
                var manifestPath = Path.Combine(targetDirectory, MANIFEST_FILE_NAME);
                PackageManifest manifest = null;

                if (File.Exists(manifestPath))
                {
                    var manifestJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                    manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, _jsonOptions);
                }

                stopwatch.Stop();

                var result = new PackageExtractionResult;
                {
                    PackageFile = packageFile,
                    TargetDirectory = targetDirectory,
                    Manifest = manifest,
                    FileCount = extractionResult.FileCount,
                    TotalSize = extractionResult.TotalSize,
                    ExtractionTime = stopwatch.Elapsed,
                    Success = true,
                    Timestamp = DateTime.UtcNow;
                };

                _logger.Info($"Package extracted successfully: {packageFile} ({stopwatch.Elapsed.TotalSeconds:F2}s)");

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                _logger.Error($"Package extraction failed: {packageFile} - {ex.Message}", ex);
                throw new PackageExtractionException($"Failed to extract package {packageFile}", ex);
            }
        }

        /// <summary>
        /// Validates a package file.
        /// </summary>
        /// <param name="packageFile">The package file to validate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The validation result.</returns>
        public async Task<PackageValidationResult> ValidatePackageAsync(
            string packageFile,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(packageFile))
                throw new ArgumentException("Package file cannot be null or empty", nameof(packageFile));

            if (!File.Exists(packageFile))
                throw new FileNotFoundException($"Package file not found: {packageFile}");

            var stopwatch = Stopwatch.StartNew();
            var result = new PackageValidationResult;
            {
                PackageFile = packageFile,
                StartTime = DateTime.UtcNow;
            };

            try
            {
                _logger.Info($"Validating package: {packageFile}");

                // Check file existence;
                result.FileExists = true;

                // Check file size;
                var fileInfo = new FileInfo(packageFile);
                result.FileSize = fileInfo.Length;

                if (fileInfo.Length > _configuration.MaxPackageSizeBytes)
                {
                    result.AddError($"Package size ({fileInfo.Length} bytes) exceeds maximum allowed size ({_configuration.MaxPackageSizeBytes} bytes)");
                }

                // Detect format;
                result.Format = DetectPackageFormat(packageFile);

                // Get format handler;
                if (!_formatHandlers.TryGetValue(result.Format.ToString(), out var handler))
                {
                    result.AddError($"Unsupported package format: {result.Format}");
                }
                else;
                {
                    // Validate package structure;
                    var structureValidation = await handler.ValidatePackageStructureAsync(
                        packageFile,
                        cancellationToken);

                    if (!structureValidation.IsValid)
                    {
                        foreach (var error in structureValidation.Errors)
                        {
                            result.AddError($"Structure validation error: {error}");
                        }
                    }

                    // Extract and validate manifest;
                    await ValidatePackageManifestAsync(packageFile, result, cancellationToken);

                    // Validate signature if present;
                    if (_configuration.ValidateSignatures)
                    {
                        await ValidatePackageSignatureAsync(packageFile, result, cancellationToken);
                    }

                    // Validate dependencies;
                    await ValidatePackageDependenciesAsync(packageFile, result, cancellationToken);
                }

                stopwatch.Stop();
                result.EndTime = DateTime.UtcNow;
                result.ValidationTime = stopwatch.Elapsed;
                result.IsValid = result.Errors.Count == 0;

                _logger.Info($"Package validation {(result.IsValid ? "passed" : "failed")}: {packageFile} ({stopwatch.Elapsed.TotalSeconds:F2}s)");

                return result;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.AddError($"Validation failed with exception: {ex.Message}");
                result.EndTime = DateTime.UtcNow;
                result.ValidationTime = stopwatch.Elapsed;
                result.IsValid = false;

                _logger.Error($"Package validation failed: {packageFile} - {ex.Message}", ex);
                return result;
            }
        }

        /// <summary>
        /// Signs a package file.
        /// </summary>
        /// <param name="packageFile">The package file to sign.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The signed package file path.</returns>
        public async Task<string> SignPackageAsync(
            string packageFile,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(packageFile))
                throw new ArgumentException("Package file cannot be null or empty", nameof(packageFile));

            if (!File.Exists(packageFile))
                throw new FileNotFoundException($"Package file not found: {packageFile}");

            var request = new PackageBuildRequest;
            {
                PackageId = Path.GetFileNameWithoutExtension(packageFile),
                Version = "1.0.0",
                Format = DetectPackageFormat(packageFile)
            };

            return await SignPackageAsync(packageFile, request, cancellationToken);
        }

        /// <summary>
        /// Gets package information without extracting.
        /// </summary>
        /// <param name="packageFile">The package file.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The package information.</returns>
        public async Task<PackageInfo> GetPackageInfoAsync(
            string packageFile,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(packageFile))
                throw new ArgumentException("Package file cannot be null or empty", nameof(packageFile));

            if (!File.Exists(packageFile))
                throw new FileNotFoundException($"Package file not found: {packageFile}");

            var format = DetectPackageFormat(packageFile);
            if (!_formatHandlers.TryGetValue(format.ToString(), out var handler))
            {
                throw new PackageFormatException($"Unsupported package format: {format}");
            }

            try
            {
                _logger.Info($"Getting package info: {packageFile}");

                // Get package info from handler;
                var info = await handler.GetPackageInfoAsync(packageFile, cancellationToken);

                // Calculate hash;
                info.Hash = await CalculatePackageHashAsync(packageFile, cancellationToken);

                // Get file info;
                var fileInfo = new FileInfo(packageFile);
                info.FileSize = fileInfo.Length;
                info.Created = fileInfo.CreationTimeUtc;
                info.Modified = fileInfo.LastWriteTimeUtc;

                // Try to read manifest if possible;
                await TryLoadManifestInfoAsync(packageFile, info, cancellationToken);

                _logger.Info($"Package info retrieved: {packageFile}");

                return info;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to get package info: {packageFile} - {ex.Message}", ex);
                throw new PackageInfoException($"Failed to get package info for {packageFile}", ex);
            }
        }

        /// <summary>
        /// Repackages a file into a different format.
        /// </summary>
        /// <param name="sourcePackage">The source package file.</param>
        /// <param name="targetFormat">The target package format.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The repackaged file path.</returns>
        public async Task<string> RepackageAsync(
            string sourcePackage,
            PackageFormat targetFormat,
            CancellationToken cancellationToken = default)
        {
            ValidateNotDisposed();
            ValidateInitialized();

            if (string.IsNullOrWhiteSpace(sourcePackage))
                throw new ArgumentException("Source package cannot be null or empty", nameof(sourcePackage));

            if (!File.Exists(sourcePackage))
                throw new FileNotFoundException($"Source package not found: {sourcePackage}");

            // Extract source package;
            var tempDir = Path.Combine(_configuration.WorkingDirectory, "repackage", Guid.NewGuid().ToString());
            Directory.CreateDirectory(tempDir);

            try
            {
                _logger.Info($"Repackaging: {sourcePackage} -> {targetFormat}");

                // Extract source package;
                await ExtractPackageAsync(sourcePackage, tempDir, cancellationToken);

                // Get package info;
                var info = await GetPackageInfoAsync(sourcePackage, cancellationToken);

                // Create build request;
                var request = new PackageBuildRequest;
                {
                    PackageId = info.Id ?? Path.GetFileNameWithoutExtension(sourcePackage),
                    Version = info.Version ?? "1.0.0",
                    Format = targetFormat,
                    SourcePath = tempDir,
                    OutputPath = Path.GetDirectoryName(sourcePackage),
                    IncludeSource = true,
                    IncludeSymbols = info.IncludeSymbols,
                    CompressionLevel = CompressionLevel.Optimal;
                };

                // Build new package;
                var result = await BuildPackageAsync(request, cancellationToken);

                _logger.Info($"Repackaging completed: {result.PackageFile}");

                return result.PackageFile;
            }
            finally
            {
                // Cleanup;
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                    // Ignore cleanup errors;
                }
            }
        }

        #endregion;

        #region Private Methods - Package Building;

        private async Task ValidateSourceAsync(
            PackageBuildRequest request,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Validating source: {request.SourcePath}");

            if (!Directory.Exists(request.SourcePath) && !File.Exists(request.SourcePath))
            {
                throw new PackageSourceException($"Source path not found: {request.SourcePath}");
            }

            // Check if source is a single file;
            if (File.Exists(request.SourcePath))
            {
                var fileInfo = new FileInfo(request.SourcePath);
                if (fileInfo.Length > _configuration.MaxPackageSizeBytes)
                {
                    throw new PackageSourceException(
                        $"Source file size ({fileInfo.Length} bytes) exceeds maximum package size");
                }
            }
            else;
            {
                // Check directory size;
                var directorySize = CalculateDirectorySize(request.SourcePath);
                if (directorySize > _configuration.MaxPackageSizeBytes)
                {
                    throw new PackageSourceException(
                        $"Source directory size ({directorySize} bytes) exceeds maximum package size");
                }

                // Check for required files;
                await ValidateSourceFilesAsync(request, cancellationToken);
            }
        }

        private async Task ValidateSourceFilesAsync(
            PackageBuildRequest request,
            CancellationToken cancellationToken)
        {
            var sourceDir = request.SourcePath;

            // Check for manifest file;
            var manifestFile = Path.Combine(sourceDir, MANIFEST_FILE_NAME);
            if (!File.Exists(manifestFile))
            {
                _logger.Warn($"No manifest file found in source: {sourceDir}");
            }

            // Check for project files if building NuGet package;
            if (request.Format == PackageFormat.NuGet)
            {
                var projectFiles = Directory.GetFiles(sourceDir, "*.csproj", SearchOption.AllDirectories);
                if (projectFiles.Length == 0)
                {
                    _logger.Warn($"No project files found for NuGet package in: {sourceDir}");
                }
            }

            await Task.CompletedTask;
        }

        private async Task<List<PackageDependency>> ResolveDependenciesAsync(
            PackageBuildRequest request,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Resolving dependencies for: {request.PackageId}");

            var dependencies = new List<PackageDependency>();

            if (!_configuration.ResolveDependencies)
            {
                return dependencies;
            }

            try
            {
                // Load dependency file if exists;
                var dependencyFile = Path.Combine(request.SourcePath, DEPENDENCIES_FILE_NAME);
                if (File.Exists(dependencyFile))
                {
                    var dependencyJson = await File.ReadAllTextAsync(dependencyFile, cancellationToken);
                    var fileDependencies = JsonSerializer.Deserialize<List<PackageDependency>>(
                        dependencyJson, _jsonOptions);

                    if (fileDependencies != null)
                    {
                        dependencies.AddRange(fileDependencies);
                    }
                }

                // Resolve dependencies;
                var resolvedDependencies = await _dependencyResolver.ResolveDependenciesAsync(
                    dependencies,
                    cancellationToken);

                // Fire event;
                OnDependenciesResolved(new DependenciesResolvedEventArgs;
                {
                    PackageId = request.PackageId,
                    PackageVersion = request.Version,
                    Dependencies = resolvedDependencies,
                    ResolvedCount = resolvedDependencies.Count;
                });

                _logger.Info($"Resolved {resolvedDependencies.Count} dependencies for: {request.PackageId}");

                return resolvedDependencies;
            }
            catch (Exception ex)
            {
                _logger.Error($"Dependency resolution failed for {request.PackageId}: {ex.Message}", ex);

                if (_configuration.FailOnDependencyResolutionError)
                {
                    throw new PackageDependencyException(
                        $"Failed to resolve dependencies for {request.PackageId}", ex);
                }

                return dependencies;
            }
        }

        private async Task<PackageMetadata> GenerateMetadataAsync(
            PackageBuildRequest request,
            List<PackageDependency> dependencies,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Generating metadata for: {request.PackageId}");

            var metadata = await _metadataGenerator.GenerateMetadataAsync(
                request,
                dependencies,
                cancellationToken);

            // Set timestamps;
            metadata.Created = DateTime.UtcNow;
            metadata.Modified = DateTime.UtcNow;
            metadata.BuildId = GenerateBuildId(request);

            return metadata;
        }

        private async Task<PackageStructure> CreatePackageStructureAsync(
            PackageBuildRequest request,
            PackageMetadata metadata,
            List<PackageDependency> dependencies,
            CancellationToken cancellationToken)
        {
            _logger.Debug($"Creating package structure for: {request.PackageId}");

            var structure = new PackageStructure;
            {
                PackageId = request.PackageId,
                Version = request.Version,
                Format = request.Format,
                RootDirectory = Path.Combine(_configuration.WorkingDirectory, "build", Guid.NewGuid().ToString()),
                Files = new List<PackageFile>(),
                Directories = new List<string>(),
                Metadata = metadata,
                Dependencies = dependencies;
            };

            // Create root directory;
            Directory.CreateDirectory(structure.RootDirectory);
            structure.Directories.Add(structure.RootDirectory);

            // Copy source files;
            await CopySourceFilesAsync(request, structure, cancellationToken);

            // Create manifest file;
            await CreateManifestFileAsync(structure, cancellationToken);

            // Create dependencies file;
            if (dependencies != null && dependencies.Count > 0)
            {
                await CreateDependenciesFileAsync(structure, cancellationToken);
            }

            // Create metadata file;
            await CreateMetadataFileAsync(structure, cancellationToken);

            // Create format-specific files;
            await CreateFormatSpecificFilesAsync(request, structure, cancellationToken);

            return structure;
        }

        private async Task CopySourceFilesAsync(
            PackageBuildRequest request,
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            var sourcePath = request.SourcePath;
            var targetPath = structure.RootDirectory;

            _logger.Debug($"Copying source files from: {sourcePath}");

            if (File.Exists(sourcePath))
            {
                // Single file;
                var targetFile = Path.Combine(targetPath, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, targetFile, true);

                structure.Files.Add(new PackageFile;
                {
                    SourcePath = sourcePath,
                    TargetPath = targetFile,
                    RelativePath = Path.GetFileName(sourcePath),
                    Size = new FileInfo(sourcePath).Length,
                    LastModified = File.GetLastWriteTimeUtc(sourcePath)
                });

                OnFileAdded(new PackageFileAddedEventArgs;
                {
                    PackageId = request.PackageId,
                    FilePath = targetFile,
                    FileSize = new FileInfo(sourcePath).Length;
                });
            }
            else if (Directory.Exists(sourcePath))
            {
                // Directory;
                await CopyDirectoryAsync(sourcePath, targetPath, structure, request, cancellationToken);
            }
        }

        private async Task CopyDirectoryAsync(
            string sourceDir,
            string targetDir,
            PackageStructure structure,
            PackageBuildRequest request,
            CancellationToken cancellationToken)
        {
            var files = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories);

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Skip files based on configuration;
                if (ShouldSkipFile(file, request))
                    continue;

                var relativePath = GetRelativePath(file, sourceDir);
                var targetFile = Path.Combine(targetDir, relativePath);

                // Create directory if needed;
                var targetFileDir = Path.GetDirectoryName(targetFile);
                if (!Directory.Exists(targetFileDir))
                {
                    Directory.CreateDirectory(targetFileDir);
                    if (!structure.Directories.Contains(targetFileDir))
                    {
                        structure.Directories.Add(targetFileDir);
                    }
                }

                // Copy file;
                File.Copy(file, targetFile, true);

                var fileInfo = new FileInfo(file);
                var packageFile = new PackageFile;
                {
                    SourcePath = file,
                    TargetPath = targetFile,
                    RelativePath = relativePath,
                    Size = fileInfo.Length,
                    LastModified = fileInfo.LastWriteTimeUtc;
                };

                structure.Files.Add(packageFile);

                OnFileAdded(new PackageFileAddedEventArgs;
                {
                    PackageId = request.PackageId,
                    FilePath = targetFile,
                    FileSize = fileInfo.Length;
                });

                // Report progress for large files;
                if (fileInfo.Length > DEFAULT_BUFFER_SIZE)
                {
                    _logger.Debug($"Copied file: {relativePath} ({fileInfo.Length} bytes)");
                }
            }

            await Task.CompletedTask;
        }

        private bool ShouldSkipFile(string filePath, PackageBuildRequest request)
        {
            var fileName = Path.GetFileName(filePath);
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            // Skip hidden files;
            if (fileName.StartsWith(".") || fileName.StartsWith("~"))
                return true;

            // Skip temporary files;
            if (extension == ".tmp" || extension == ".temp" || fileName.EndsWith(".tmp"))
                return true;

            // Skip backup files;
            if (extension == ".bak" || extension == ".backup")
                return true;

            // Skip log files unless configured;
            if (extension == ".log" && !request.IncludeLogs)
                return true;

            // Skip symbol files unless configured;
            if ((extension == ".pdb" || extension == ".dbg") && !request.IncludeSymbols)
                return true;

            // Skip source files unless configured;
            if ((extension == ".cs" || extension == ".vb" || extension == ".fs") && !request.IncludeSource)
                return true;

            // Check against exclude patterns;
            if (request.ExcludePatterns != null)
            {
                foreach (var pattern in request.ExcludePatterns)
                {
                    if (MatchesPattern(filePath, pattern))
                        return true;
                }
            }

            return false;
        }

        private bool MatchesPattern(string filePath, string pattern)
        {
            // Simple pattern matching - can be enhanced with regex;
            if (pattern.Contains("*") || pattern.Contains("?"))
            {
                // Convert to regex pattern;
                var regexPattern = "^" + Regex.Escape(pattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";

                return Regex.IsMatch(filePath, regexPattern, RegexOptions.IgnoreCase);
            }

            return filePath.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        }

        private async Task CreateManifestFileAsync(
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            var manifest = new PackageManifest;
            {
                Id = structure.PackageId,
                Version = structure.Version,
                Format = structure.Format,
                Created = DateTime.UtcNow,
                Files = structure.Files.Select(f => new PackageManifestFile;
                {
                    Path = f.RelativePath,
                    Size = f.Size,
                    Hash = CalculateFileHash(f.SourcePath),
                    LastModified = f.LastModified;
                }).ToList(),
                Metadata = structure.Metadata,
                Dependencies = structure.Dependencies;
            };

            var manifestFile = Path.Combine(structure.RootDirectory, MANIFEST_FILE_NAME);
            var manifestJson = JsonSerializer.Serialize(manifest, _jsonOptions);

            await File.WriteAllTextAsync(manifestFile, manifestJson, cancellationToken);

            structure.Files.Add(new PackageFile;
            {
                SourcePath = manifestFile,
                TargetPath = manifestFile,
                RelativePath = MANIFEST_FILE_NAME,
                Size = manifestJson.Length,
                LastModified = DateTime.UtcNow;
            });
        }

        private async Task CreateDependenciesFileAsync(
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            if (structure.Dependencies == null || structure.Dependencies.Count == 0)
                return;

            var dependenciesFile = Path.Combine(structure.RootDirectory, DEPENDENCIES_FILE_NAME);
            var dependenciesJson = JsonSerializer.Serialize(structure.Dependencies, _jsonOptions);

            await File.WriteAllTextAsync(dependenciesFile, dependenciesJson, cancellationToken);

            structure.Files.Add(new PackageFile;
            {
                SourcePath = dependenciesFile,
                TargetPath = dependenciesFile,
                RelativePath = DEPENDENCIES_FILE_NAME,
                Size = dependenciesJson.Length,
                LastModified = DateTime.UtcNow;
            });
        }

        private async Task CreateMetadataFileAsync(
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            var metadataFile = Path.Combine(structure.RootDirectory, METADATA_FILE_NAME);
            var metadataJson = JsonSerializer.Serialize(structure.Metadata, _jsonOptions);

            await File.WriteAllTextAsync(metadataFile, metadataJson, cancellationToken);

            structure.Files.Add(new PackageFile;
            {
                SourcePath = metadataFile,
                TargetPath = metadataFile,
                RelativePath = METADATA_FILE_NAME,
                Size = metadataJson.Length,
                LastModified = DateTime.UtcNow;
            });
        }

        private async Task CreateFormatSpecificFilesAsync(
            PackageBuildRequest request,
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            switch (request.Format)
            {
                case PackageFormat.NuGet:
                    await CreateNuSpecFileAsync(request, structure, cancellationToken);
                    break;

                case PackageFormat.Zip:
                    // No special files needed for ZIP;
                    break;

                case PackageFormat.Tar:
                case PackageFormat.TarGz:
                case PackageFormat.TarBz2:
                    // No special files needed for TAR formats;
                    break;

                case PackageFormat.Msi:
                    await CreateMsiFilesAsync(request, structure, cancellationToken);
                    break;

                case PackageFormat.Deb:
                case PackageFormat.Rpm:
                    await CreateLinuxPackageFilesAsync(request, structure, cancellationToken);
                    break;

                default:
                    _logger.Warn($"No format-specific files for format: {request.Format}");
                    break;
            }
        }

        private async Task CreateNuSpecFileAsync(
            PackageBuildRequest request,
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            var nuspecFile = Path.Combine(structure.RootDirectory, $"{request.PackageId}{NUSPEC_EXTENSION}");

            var nuspec = new NuGetSpecification;
            {
                Id = request.PackageId,
                Version = request.Version,
                Title = structure.Metadata?.Title ?? request.PackageId,
                Authors = structure.Metadata?.Authors ?? new List<string> { "NEDA" },
                Description = structure.Metadata?.Description ?? $"Package for {request.PackageId}",
                Copyright = structure.Metadata?.Copyright ?? $"Copyright {DateTime.UtcNow.Year}",
                Tags = structure.Metadata?.Tags ?? new List<string> { "nuda" },
                Dependencies = structure.Dependencies?.Select(d => new NuGetDependency;
                {
                    Id = d.PackageId,
                    Version = d.VersionRange;
                }).ToList()
            };

            var nuspecXml = SerializeNuSpec(nuspec);
            await File.WriteAllTextAsync(nuspecFile, nuspecXml, cancellationToken);

            structure.Files.Add(new PackageFile;
            {
                SourcePath = nuspecFile,
                TargetPath = nuspecFile,
                RelativePath = $"{request.PackageId}{NUSPEC_EXTENSION}",
                Size = nuspecXml.Length,
                LastModified = DateTime.UtcNow;
            });
        }

        private async Task CreateMsiFilesAsync(
            PackageBuildRequest request,
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            // Create MSI WXS file (WiX source)
            var wxsFile = Path.Combine(structure.RootDirectory, $"{request.PackageId}.wxs");

            var wxsContent = GenerateWxsFile(request, structure);
            await File.WriteAllTextAsync(wxsFile, wxsContent, cancellationToken);

            structure.Files.Add(new PackageFile;
            {
                SourcePath = wxsFile,
                TargetPath = wxsFile,
                RelativePath = $"{request.PackageId}.wxs",
                Size = wxsContent.Length,
                LastModified = DateTime.UtcNow;
            });
        }

        private async Task CreateLinuxPackageFilesAsync(
            PackageBuildRequest request,
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            // Create control file for DEB packages;
            if (request.Format == PackageFormat.Deb)
            {
                var controlFile = Path.Combine(structure.RootDirectory, "control");
                var controlContent = GenerateDebControlFile(request, structure);
                await File.WriteAllTextAsync(controlFile, controlContent, cancellationToken);

                structure.Files.Add(new PackageFile;
                {
                    SourcePath = controlFile,
                    TargetPath = controlFile,
                    RelativePath = "control",
                    Size = controlContent.Length,
                    LastModified = DateTime.UtcNow;
                });
            }

            // Create spec file for RPM packages;
            if (request.Format == PackageFormat.Rpm)
            {
                var specFile = Path.Combine(structure.RootDirectory, $"{request.PackageId}.spec");
                var specContent = GenerateRpmSpecFile(request, structure);
                await File.WriteAllTextAsync(specFile, specContent, cancellationToken);

                structure.Files.Add(new PackageFile;
                {
                    SourcePath = specFile,
                    TargetPath = specFile,
                    RelativePath = $"{request.PackageId}.spec",
                    Size = specContent.Length,
                    LastModified = DateTime.UtcNow;
                });
            }
        }

        private async Task<string> BuildPackageByFormatAsync(
            PackageBuildRequest request,
            PackageStructure structure,
            CancellationToken cancellationToken)
        {
            var formatKey = request.Format.ToString();

            if (!_formatHandlers.TryGetValue(formatKey, out var handler))
            {
                throw new PackageFormatException($"Unsupported package format: {request.Format}");
            }

            // Determine output file path;
            var outputFile = GetOutputFilePath(request, structure);

            _logger.Info($"Building package with format handler: {formatKey} -> {outputFile}");

            // Build package using format handler;
            var packageFile = await handler.BuildPackageAsync(
                structure.RootDirectory,
                outputFile,
                request.CompressionLevel,
                cancellationToken);

            return packageFile;
        }

        private async Task ValidatePackageAsync(
            string packageFile,
            PackageBuildRequest request,
            CancellationToken cancellationToken)
        {
            if (!_configuration.ValidateAfterBuild)
                return;

            _logger.Debug($"Validating built package: {packageFile}");

            var validationResult = await ValidatePackageAsync(packageFile, cancellationToken);

            if (!validationResult.IsValid)
            {
                throw new PackageValidationException(
                    $"Package validation failed with {validationResult.Errors.Count} errors",
                    validationResult.Errors);
            }
        }

        private async Task<string> SignPackageAsync(
            string packageFile,
            PackageBuildRequest request,
            CancellationToken cancellationToken)
        {
            if (!_configuration.SignPackages)
                return packageFile;

            _logger.Info($"Signing package: {packageFile}");

            try
            {
                var signedPackage = await _signer.SignPackageAsync(packageFile, cancellationToken);

                // Fire event;
                OnPackageSigned(new PackageSignedEventArgs;
                {
                    PackageId = request.PackageId,
                    PackageVersion = request.Version,
                    OriginalFile = packageFile,
                    SignedFile = signedPackage,
                    SignatureAlgorithm = _signer.SignatureAlgorithm,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.Info($"Package signed successfully: {signedPackage}");

                return signedPackage;
            }
            catch (Exception ex)
            {
                _logger.Error($"Package signing failed: {ex.Message}", ex);

                if (_configuration.FailOnSigningError)
                {
                    throw new PackageSigningException($"Failed to sign package {packageFile}", ex);
                }

                return packageFile;
            }
        }

        #endregion;

        #region Private Methods - Helper Methods;

        private void InitializeFormatHandlers()
        {
            // Register default format handlers;
            RegisterFormatHandler(PackageFormat.Zip.ToString(), new ZipPackageHandler());
            RegisterFormatHandler(PackageFormat.NuGet.ToString(), new NuGetPackageHandler());
            RegisterFormatHandler(PackageFormat.Tar.ToString(), new TarPackageHandler());
            RegisterFormatHandler(PackageFormat.TarGz.ToString(), new TarGzPackageHandler());

            // Try to register platform-specific handlers;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                try
                {
                    RegisterFormatHandler(PackageFormat.Msi.ToString(), new MsiPackageHandler());
                }
                catch (Exception ex)
                {
                    _logger.Debug($"MSI package handler not available: {ex.Message}");
                }
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    RegisterFormatHandler(PackageFormat.Deb.ToString(), new DebPackageHandler());
                    RegisterFormatHandler(PackageFormat.Rpm.ToString(), new RpmPackageHandler());
                }
                catch (Exception ex)
                {
                    _logger.Debug($"Linux package handlers not available: {ex.Message}");
                }
            }
        }

        private async Task InitializeFormatHandlersAsync(CancellationToken cancellationToken)
        {
            foreach (var handler in _formatHandlers.Values)
            {
                try
                {
                    await handler.InitializeAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.Error($"Failed to initialize format handler {handler.GetType().Name}: {ex.Message}", ex);
                }
            }
        }

        private void CreateWorkingDirectory()
        {
            if (!Directory.Exists(_configuration.WorkingDirectory))
            {
                Directory.CreateDirectory(_configuration.WorkingDirectory);
            }

            // Create subdirectories;
            var subDirs = new[]
            {
                Path.Combine(_configuration.WorkingDirectory, "build"),
                Path.Combine(_configuration.WorkingDirectory, "cache"),
                Path.Combine(_configuration.WorkingDirectory, "output"),
                Path.Combine(_configuration.WorkingDirectory, "temp")
            };

            foreach (var dir in subDirs)
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }

        private string GenerateBuildId(PackageBuildRequest request)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var random = new Random().Next(1000, 9999);
            return $"{request.PackageId}-{request.Version}-{timestamp}-{random}";
        }

        private PackageFormat DetectPackageFormat(string packageFile)
        {
            var extension = Path.GetExtension(packageFile).ToLowerInvariant();

            return extension switch;
            {
                ".nupkg" => PackageFormat.NuGet,
                ".zip" => PackageFormat.Zip,
                ".tar" => PackageFormat.Tar,
                ".tar.gz" or ".tgz" => PackageFormat.TarGz,
                ".tar.bz2" or ".tbz2" => PackageFormat.TarBz2,
                ".msi" => PackageFormat.Msi,
                ".deb" => PackageFormat.Deb,
                ".rpm" => PackageFormat.Rpm,
                ".7z" => PackageFormat.SevenZip,
                ".cab" => PackageFormat.Cab,
                _ => PackageFormat.Unknown;
            };
        }

        private string GetOutputFilePath(PackageBuildRequest request, PackageStructure structure)
        {
            string outputDir;

            if (!string.IsNullOrWhiteSpace(request.OutputPath))
            {
                outputDir = request.OutputPath;
                Directory.CreateDirectory(outputDir);
            }
            else;
            {
                outputDir = Path.Combine(_configuration.WorkingDirectory, "output");
            }

            var fileName = $"{request.PackageId}.{request.Version}";

            // Add format-specific extension;
            fileName += GetFormatExtension(request.Format);

            return Path.Combine(outputDir, fileName);
        }

        private string GetFormatExtension(PackageFormat format)
        {
            return format switch;
            {
                PackageFormat.NuGet => NUPKG_EXTENSION,
                PackageFormat.Zip => ZIP_EXTENSION,
                PackageFormat.Tar => ".tar",
                PackageFormat.TarGz => ".tar.gz",
                PackageFormat.TarBz2 => ".tar.bz2",
                PackageFormat.Msi => ".msi",
                PackageFormat.Deb => ".deb",
                PackageFormat.Rpm => ".rpm",
                PackageFormat.SevenZip => ".7z",
                PackageFormat.Cab => ".cab",
                _ => ZIP_EXTENSION;
            };
        }

        private async Task<string> CalculatePackageHashAsync(
            string packageFile,
            CancellationToken cancellationToken)
        {
            using var stream = File.OpenRead(packageFile);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private string CalculateFileHash(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            using var sha256 = SHA256.Create();
            var hashBytes = sha256.ComputeHash(stream);
            return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
        }

        private long CalculateDirectorySize(string directoryPath)
        {
            long size = 0;

            try
            {
                var files = Directory.GetFiles(directoryPath, "*.*", SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    try
                    {
                        size += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Skip files that can't be accessed;
                    }
                }
            }
            catch
            {
                // Return 0 if directory can't be accessed;
            }

            return size;
        }

        private string GetRelativePath(string fullPath, string basePath)
        {
            var uri1 = new Uri(fullPath);
            var uri2 = new Uri(basePath + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(uri2.MakeRelativeUri(uri1).ToString());
        }

        private async Task ValidatePackageManifestAsync(
            string packageFile,
            PackageValidationResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                // Try to extract and validate manifest;
                var formatKey = result.Format.ToString();
                if (_formatHandlers.TryGetValue(formatKey, out var handler))
                {
                    var manifest = await handler.ExtractManifestAsync(packageFile, cancellationToken);
                    if (manifest != null)
                    {
                        result.Manifest = manifest;

                        // Validate manifest content;
                        if (string.IsNullOrWhiteSpace(manifest.Id))
                        {
                            result.AddError("Manifest missing package ID");
                        }

                        if (string.IsNullOrWhiteSpace(manifest.Version))
                        {
                            result.AddError("Manifest missing package version");
                        }

                        if (!Version.TryParse(manifest.Version, out _))
                        {
                            result.AddWarning($"Package version format may be invalid: {manifest.Version}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Failed to validate manifest: {ex.Message}");
            }
        }

        private async Task ValidatePackageSignatureAsync(
            string packageFile,
            PackageValidationResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                var isValid = await _signer.ValidateSignatureAsync(packageFile, cancellationToken);
                result.IsSigned = isValid;

                if (!isValid && _configuration.RequireSignature)
                {
                    result.AddError("Package signature validation failed");
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Signature validation failed: {ex.Message}");
            }
        }

        private async Task ValidatePackageDependenciesAsync(
            string packageFile,
            PackageValidationResult result,
            CancellationToken cancellationToken)
        {
            try
            {
                var dependencies = await _dependencyResolver.GetPackageDependenciesAsync(
                    packageFile,
                    cancellationToken);

                if (dependencies != null)
                {
                    result.Dependencies = dependencies;

                    // Check for missing dependencies;
                    var missingDeps = await _dependencyResolver.FindMissingDependenciesAsync(
                        dependencies,
                        cancellationToken);

                    if (missingDeps.Count > 0)
                    {
                        foreach (var dep in missingDeps)
                        {
                            result.AddWarning($"Missing dependency: {dep.PackageId} {dep.VersionRange}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.AddWarning($"Dependency validation failed: {ex.Message}");
            }
        }

        private async Task TryLoadManifestInfoAsync(
            string packageFile,
            PackageInfo info,
            CancellationToken cancellationToken)
        {
            try
            {
                var formatKey = DetectPackageFormat(packageFile).ToString();
                if (_formatHandlers.TryGetValue(formatKey, out var handler))
                {
                    var manifest = await handler.ExtractManifestAsync(packageFile, cancellationToken);
                    if (manifest != null)
                    {
                        info.Id = manifest.Id;
                        info.Version = manifest.Version;
                        info.Description = manifest.Metadata?.Description;
                        info.Authors = manifest.Metadata?.Authors;
                        info.Copyright = manifest.Metadata?.Copyright;
                        info.Tags = manifest.Metadata?.Tags;
                        info.FileCount = manifest.Files?.Count ?? 0;
                        info.DependencyCount = manifest.Dependencies?.Count ?? 0;
                    }
                }
            }
            catch
            {
                // Ignore errors when loading manifest info;
            }
        }

        private string SerializeNuSpec(NuGetSpecification nuspec)
        {
            var ns = "http://schemas.microsoft.com/packaging/2010/07/nuspec.xsd";
            var doc = new XmlDocument();

            var declaration = doc.CreateXmlDeclaration("1.0", "utf-8", null);
            doc.AppendChild(declaration);

            var package = doc.CreateElement("package", ns);
            doc.AppendChild(package);

            var metadata = doc.CreateElement("metadata", ns);
            package.AppendChild(metadata);

            // Add metadata elements;
            AddXmlElement(doc, metadata, "id", nuspec.Id);
            AddXmlElement(doc, metadata, "version", nuspec.Version);
            AddXmlElement(doc, metadata, "title", nuspec.Title);
            AddXmlElement(doc, metadata, "authors", string.Join(", ", nuspec.Authors));
            AddXmlElement(doc, metadata, "description", nuspec.Description);
            AddXmlElement(doc, metadata, "copyright", nuspec.Copyright);
            AddXmlElement(doc, metadata, "tags", string.Join(" ", nuspec.Tags));

            // Add dependencies if any;
            if (nuspec.Dependencies != null && nuspec.Dependencies.Count > 0)
            {
                var dependencies = doc.CreateElement("dependencies", ns);
                metadata.AppendChild(dependencies);

                foreach (var dep in nuspec.Dependencies)
                {
                    var dependency = doc.CreateElement("dependency", ns);
                    dependency.SetAttribute("id", dep.Id);
                    dependency.SetAttribute("version", dep.Version);
                    dependencies.AppendChild(dependency);
                }
            }

            using var writer = new StringWriter();
            using var xmlWriter = XmlWriter.Create(writer, new XmlWriterSettings;
            {
                Indent = true,
                IndentChars = "  ",
                OmitXmlDeclaration = false;
            });

            doc.Save(xmlWriter);
            return writer.ToString();
        }

        private void AddXmlElement(XmlDocument doc, XmlElement parent, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                var element = doc.CreateElement(name, parent.NamespaceURI);
                element.InnerText = value;
                parent.AppendChild(element);
            }
        }

        private string GenerateWxsFile(PackageBuildRequest request, PackageStructure structure)
        {
            return $@"<?xml version='1.0' encoding='UTF-8'?>
<Wix xmlns='http://schemas.microsoft.com/wix/2006/wi'>
  <Product Id='*' 
           Name='{request.PackageId}' 
           Language='1033' 
           Version='{request.Version}'
           Manufacturer='{structure.Metadata?.Company ?? "NEDA"}' 
           UpgradeCode='{Guid.NewGuid()}'>
    
    <Package InstallerVersion='200' 
             Compressed='yes' 
             InstallScope='perMachine' />
    
    <MajorUpgrade DowngradeErrorMessage='A newer version of [ProductName] is already installed.' />
    
    <MediaTemplate EmbedCab='yes' />
    
    <Feature Id='ProductFeature' Title='{request.PackageId}' Level='1'>
      <ComponentGroupRef Id='ProductComponents' />
    </Feature>
  </Product>
  
  <Fragment>
    <Directory Id='TARGETDIR' Name='SourceDir'>
      <Directory Id='ProgramFilesFolder'>
        <Directory Id='INSTALLFOLDER' Name='{request.PackageId}' />
      </Directory>
    </Directory>
  </Fragment>
  
  <Fragment>
    <ComponentGroup Id='ProductComponents' Directory='INSTALLFOLDER'>
      <!-- Files will be added here -->
    </ComponentGroup>
  </Fragment>
</Wix>";
        }

        private string GenerateDebControlFile(PackageBuildRequest request, PackageStructure structure)
        {
            return $@"Package: {request.PackageId}
Version: {request.Version}
Section: utils;
Priority: optional;
Architecture: all;
Maintainer: {structure.Metadata?.Authors?.FirstOrDefault() ?? "NEDA Team"} <support@neda.local>
Description: {structure.Metadata?.Description ?? $"Package for {request.PackageId}"}
 {structure.Metadata?.Description ?? string.Empty}
Depends: {GetDebDependencies(structure.Dependencies)}
";
        }

        private string GetDebDependencies(List<PackageDependency> dependencies)
        {
            if (dependencies == null || dependencies.Count == 0)
                return "debconf (>= 0.5)";

            var depList = new List<string>();
            foreach (var dep in dependencies)
            {
                depList.Add($"{dep.PackageId} (>= {dep.VersionRange})");
            }

            return string.Join(", ", depList);
        }

        private string GenerateRpmSpecFile(PackageBuildRequest request, PackageStructure structure)
        {
            return $@"Name: {request.PackageId}
Version: {request.Version.Replace("-", "_")}
Release: 1%{{?dist}}
Summary: {structure.Metadata?.Description ?? $"Package for {request.PackageId}"}
License: {structure.Metadata?.License ?? "Proprietary"}
URL: {structure.Metadata?.ProjectUrl ?? "https://neda.local"}
Source0: %{{name}}-%{{version}}.tar.gz;

BuildRequires: {GetRpmBuildRequires(structure.Dependencies)}
Requires: {GetRpmRequires(structure.Dependencies)}

%description;
{structure.Metadata?.Description ?? string.Empty}

%prep;
%setup -q;

%build;
# Build commands here;

%install;
# Install commands here;

%files;
# Files list here;

%changelog;
* {DateTime.UtcNow:ddd MMM dd yyyy} {structure.Metadata?.Authors?.FirstOrDefault() ?? "NEDA Team"} - {request.Version}-1;
- Initial package;
";
        }

        private string GetRpmBuildRequires(List<PackageDependency> dependencies)
        {
            // RPM build requires logic;
            return "gcc, make";
        }

        private string GetRpmRequires(List<PackageDependency> dependencies)
        {
            if (dependencies == null || dependencies.Count == 0)
                return "/bin/sh";

            var depList = new List<string>();
            foreach (var dep in dependencies)
            {
                depList.Add($"{dep.PackageId} >= {dep.VersionRange}");
            }

            return string.Join(", ", depList);
        }

        #endregion;

        #region Private Methods - Event Handling;

        private void OnBuildStarted(PackageBuildStartedEventArgs e)
        {
            BuildStarted?.Invoke(this, e);
        }

        private void OnBuildCompleted(PackageBuildCompletedEventArgs e)
        {
            BuildCompleted?.Invoke(this, e);
        }

        private void OnBuildFailed(PackageBuildFailedEventArgs e)
        {
            BuildFailed?.Invoke(this, e);
        }

        private void OnFileAdded(PackageFileAddedEventArgs e)
        {
            FileAdded?.Invoke(this, e);
        }

        private void OnPackageSigned(PackageSignedEventArgs e)
        {
            PackageSigned?.Invoke(this, e);
        }

        private void OnDependenciesResolved(DependenciesResolvedEventArgs e)
        {
            DependenciesResolved?.Invoke(this, e);
        }

        #endregion;

        #region Validation Methods;

        private void ValidateNotDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PackageBuilder));
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
                throw new InvalidOperationException("PackageBuilder is not initialized");
        }

        #endregion;

        #region IDisposable Implementation;

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Dispose format handlers;
                    foreach (var handler in _formatHandlers.Values)
                    {
                        handler.Dispose();
                    }

                    // Dispose semaphore;
                    _buildSemaphore?.Dispose();

                    // Dispose dependencies;
                    _validator?.Dispose();
                    _signer?.Dispose();
                    _dependencyResolver?.Dispose();
                    _metadataGenerator?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~PackageBuilder()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Types;

        /// <summary>
        /// Package structure representation.
        /// </summary>
        private class PackageStructure;
        {
            public string PackageId { get; set; }
            public string Version { get; set; }
            public PackageFormat Format { get; set; }
            public string RootDirectory { get; set; }
            public List<PackageFile> Files { get; set; }
            public List<string> Directories { get; set; }
            public PackageMetadata Metadata { get; set; }
            public List<PackageDependency> Dependencies { get; set; }
        }

        /// <summary>
        /// Package file information.
        /// </summary>
        private class PackageFile;
        {
            public string SourcePath { get; set; }
            public string TargetPath { get; set; }
            public string RelativePath { get; set; }
            public long Size { get; set; }
            public DateTime LastModified { get; set; }
        }

        /// <summary>
        /// NuGet specification.
        /// </summary>
        private class NuGetSpecification;
        {
            public string Id { get; set; }
            public string Version { get; set; }
            public string Title { get; set; }
            public List<string> Authors { get; set; }
            public string Description { get; set; }
            public string Copyright { get; set; }
            public List<string> Tags { get; set; }
            public List<NuGetDependency> Dependencies { get; set; }
        }

        /// <summary>
        /// NuGet dependency.
        /// </summary>
        private class NuGetDependency;
        {
            public string Id { get; set; }
            public string Version { get; set; }
        }

        #endregion;
    }
}
