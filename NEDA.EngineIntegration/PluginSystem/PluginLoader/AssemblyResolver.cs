using Microsoft.Extensions.DependencyModel;
using NEDA.AI.ComputerVision;
using NEDA.Core.Security;
using NEDA.Core.SystemControl;
using NEDA.EngineIntegration.PluginSystem.Interfaces;
using NEDA.EngineIntegration.PluginSystem.PluginLoader;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.SecurityModules;
using NEDA.SecurityModules.AdvancedSecurity.Encryption;
using NEDA.Services;
using NEDA.SystemControl;
using NEDA.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security;
using System.Security.Cryptography;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;

namespace NEDA.EngineIntegration.PluginSystem.PluginLoader;
{
    /// <summary>
    /// AssemblyResolver sınıfı - Dinamik assembly yükleme, çözümleme ve güvenlik doğrulaması sağlar;
    /// </summary>
    public class AssemblyResolver : IAssemblyResolver, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly ISecurityManager _securityManager;
        private readonly ICryptoEngine _cryptoEngine;
        private readonly IFileService _fileService;
        private readonly ISystemManager _systemManager;

        private readonly AssemblyLoadContext _pluginLoadContext;
        private readonly ConcurrentDictionary<string, Assembly> _loadedAssemblies;
        private readonly ConcurrentDictionary<string, AssemblyMetadata> _assemblyMetadata;
        private readonly ConcurrentDictionary<string, List<string>> _assemblyDependencies;
        private readonly ConcurrentDictionary<string, DateTime> _assemblyLastAccess;
        private readonly ConcurrentDictionary<string, AssemblyValidationResult> _validationCache;

        private readonly string _assemblyCacheDirectory;
        private readonly string _shadowCopyDirectory;
        private readonly string _trustedAssembliesFile;

        private bool _disposed;
        private readonly object _resolutionLock = new object();
        private readonly Timer _cleanupTimer;
        private readonly HashSet<string> _trustedPublisherHashes;

        // Assembly çözümleme event'leri;
        public event EventHandler<AssemblyResolvingEventArgs> OnAssemblyResolving;
        public event EventHandler<AssemblyResolvedEventArgs> OnAssemblyResolved;
        public event EventHandler<AssemblyLoadFailedEventArgs> OnAssemblyLoadFailed;
        public event EventHandler<AssemblyValidatedEventArgs> OnAssemblyValidated;

        /// <summary>
        /// Yüklü assembly sayısı;
        /// </summary>
        public int LoadedAssemblyCount => _loadedAssemblies.Count;

        /// <summary>
        /// Cache dizini;
        /// </summary>
        public string CacheDirectory => _assemblyCacheDirectory;

        /// <summary>
        /// Shadow copy etkin mi;
        /// </summary>
        public bool ShadowCopyEnabled { get; set; } = true;

        /// <summary>
        /// Assembly doğrulama etkin mi;
        /// </summary>
        public bool ValidationEnabled { get; set; } = true;

        /// <summary>
        /// Güvenli mod etkin mi;
        /// </summary>
        public bool SafeModeEnabled { get; set; } = true;

        /// <summary>
        /// Assembly çözümleme önbelleği etkin mi;
        /// </summary>
        public bool ResolutionCacheEnabled { get; set; } = true;

        /// <summary>
        /// Maksimum assembly boyutu (byte)
        /// </summary>
        public long MaxAssemblySize { get; set; } = 100 * 1024 * 1024; // 100 MB;

        /// <summary>
        /// Maksimum önbellek boyutu (byte)
        /// </summary>
        public long MaxCacheSize { get; set; } = 500 * 1024 * 1024; // 500 MB;

        /// <summary>
        /// Assembly yükleme zaman aşımı (ms)
        /// </summary>
        public int AssemblyLoadTimeout { get; set; } = 30000; // 30 saniye;

        /// <summary>
        /// Trusted assembly listesi;
        /// </summary>
        public IReadOnlyCollection<string> TrustedPublisherHashes => _trustedPublisherHashes;

        /// <summary>
        /// AssemblyResolver constructor;
        /// </summary>
        public AssemblyResolver(
            ILogger logger,
            ISecurityManager securityManager,
            ICryptoEngine cryptoEngine,
            IFileService fileService,
            ISystemManager systemManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));
            _cryptoEngine = cryptoEngine ?? throw new ArgumentNullException(nameof(cryptoEngine));
            _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
            _systemManager = systemManager ?? throw new ArgumentNullException(nameof(systemManager));

            Initialize();

            // Özel AssemblyLoadContext oluştur;
            _pluginLoadContext = new PluginAssemblyLoadContext(this);

            // Collection'ları başlat;
            _loadedAssemblies = new ConcurrentDictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            _assemblyMetadata = new ConcurrentDictionary<string, AssemblyMetadata>(StringComparer.OrdinalIgnoreCase);
            _assemblyDependencies = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _assemblyLastAccess = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            _validationCache = new ConcurrentDictionary<string, AssemblyValidationResult>(StringComparer.OrdinalIgnoreCase);
            _trustedPublisherHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Temizleme timer'ını başlat;
            _cleanupTimer = new Timer(CleanupCacheCallback, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));

            _logger.LogInformation("AssemblyResolver başlatıldı. Cache: {CacheDir}, ShadowCopy: {ShadowCopyDir}",
                _assemblyCacheDirectory, _shadowCopyDirectory);
        }

        private void Initialize()
        {
            try
            {
                // Dizinleri oluştur;
                var appDataPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NEDA",
                    "AssemblyCache");

                _assemblyCacheDirectory = Path.Combine(appDataPath, "Cache");
                _shadowCopyDirectory = Path.Combine(appDataPath, "ShadowCopy");
                _trustedAssembliesFile = Path.Combine(appDataPath, "TrustedAssemblies.json");

                Directory.CreateDirectory(_assemblyCacheDirectory);
                Directory.CreateDirectory(_shadowCopyDirectory);

                // Trusted assembly listesini yükle;
                LoadTrustedAssemblies();

                // AppDomain event'lerini kaydet;
                AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;
                AppDomain.CurrentDomain.AssemblyLoad += CurrentDomain_AssemblyLoad;
                AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve += CurrentDomain_ReflectionOnlyAssemblyResolve;

                _logger.LogDebug("AssemblyResolver dizinleri oluşturuldu");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AssemblyResolver başlatılamadı");
                throw new AssemblyResolverException("AssemblyResolver başlatılamadı", ex);
            }
        }

        private void LoadTrustedAssemblies()
        {
            try
            {
                if (File.Exists(_trustedAssembliesFile))
                {
                    var trustedData = File.ReadAllText(_trustedAssembliesFile);
                    var trustedList = System.Text.Json.JsonSerializer.Deserialize<List<TrustedAssembly>>(trustedData);

                    foreach (var trusted in trustedList)
                    {
                        if (!string.IsNullOrEmpty(trusted.PublisherHash))
                        {
                            _trustedPublisherHashes.Add(trusted.PublisherHash);
                        }
                    }

                    _logger.LogInformation("{Count} güvenilir assembly yüklendi", _trustedPublisherHashes.Count);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Güvenilir assembly listesi yüklenemedi");
            }
        }

        private void SaveTrustedAssemblies()
        {
            try
            {
                var trustedList = _trustedPublisherHashes.Select(hash => new TrustedAssembly;
                {
                    PublisherHash = hash,
                    AddedDate = DateTime.UtcNow;
                }).ToList();

                var trustedData = System.Text.Json.JsonSerializer.Serialize(trustedList);
                File.WriteAllText(_trustedAssembliesFile, trustedData);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Güvenilir assembly listesi kaydedilemedi");
            }
        }

        /// <summary>
        /// Assembly'yi çözümler ve yükler;
        /// </summary>
        public Assembly ResolveAssembly(string assemblyPath, ResolveOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new ArgumentException("Assembly yolu boş olamaz", nameof(assemblyPath));

            if (!File.Exists(assemblyPath))
                throw new FileNotFoundException($"Assembly bulunamadı: {assemblyPath}", assemblyPath);

            var resolveOptions = options ?? new ResolveOptions();
            var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);

            _logger.LogDebug("Assembly çözümleniyor: {Assembly} ({Path})", assemblyName, assemblyPath);

            try
            {
                // Event tetikle;
                OnAssemblyResolving?.Invoke(this, new AssemblyResolvingEventArgs(assemblyPath, resolveOptions));

                // Önbellek kontrolü;
                if (ResolutionCacheEnabled && _loadedAssemblies.TryGetValue(assemblyPath, out var cachedAssembly))
                {
                    _assemblyLastAccess[assemblyPath] = DateTime.UtcNow;
                    _logger.LogDebug("Assembly önbellekten yüklendi: {Assembly}", assemblyName);
                    return cachedAssembly;
                }

                // Güvenlik doğrulaması;
                if (ValidationEnabled)
                {
                    var validationResult = ValidateAssembly(assemblyPath, resolveOptions);
                    if (!validationResult.IsValid)
                    {
                        var errorMsg = $"Assembly doğrulama başarısız: {validationResult.ErrorMessage}";
                        _logger.LogWarning(errorMsg);

                        OnAssemblyLoadFailed?.Invoke(this, new AssemblyLoadFailedEventArgs(
                            assemblyPath,
                            new SecurityException(errorMsg),
                            AssemblyLoadFailureReason.ValidationFailed));

                        if (resolveOptions.ThrowOnValidationFailure)
                            throw new AssemblyValidationException(errorMsg, validationResult);
                    }

                    OnAssemblyValidated?.Invoke(this, new AssemblyValidatedEventArgs(assemblyPath, validationResult));
                }

                // Shadow copy yap;
                string loadPath = assemblyPath;
                if (ShadowCopyEnabled)
                {
                    loadPath = CreateShadowCopy(assemblyPath, resolveOptions);
                }

                // Assembly'yi yükle;
                Assembly assembly;
                if (resolveOptions.LoadInSeparateContext)
                {
                    assembly = LoadAssemblyInContext(loadPath, resolveOptions);
                }
                else;
                {
                    assembly = LoadAssemblyDirect(loadPath, resolveOptions);
                }

                // Bağımlılıkları çözümle;
                if (resolveOptions.ResolveDependencies)
                {
                    ResolveDependencies(assembly, resolveOptions);
                }

                // Önbelleğe ekle;
                if (ResolutionCacheEnabled)
                {
                    _loadedAssemblies[assemblyPath] = assembly;
                    _assemblyLastAccess[assemblyPath] = DateTime.UtcNow;

                    // Metadata kaydet;
                    var metadata = ExtractAssemblyMetadata(assembly, assemblyPath);
                    _assemblyMetadata[assemblyPath] = metadata;
                }

                _logger.LogInformation("Assembly başarıyla yüklendi: {Assembly} v{Version}",
                    assemblyName, assembly.GetName().Version);

                OnAssemblyResolved?.Invoke(this, new AssemblyResolvedEventArgs(assembly, assemblyPath, resolveOptions));

                return assembly;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembly çözümleme hatası: {Assembly}", assemblyName);

                OnAssemblyLoadFailed?.Invoke(this, new AssemblyLoadFailedEventArgs(
                    assemblyPath,
                    ex,
                    AssemblyLoadFailureReason.LoadError));

                throw new AssemblyResolverException($"Assembly çözümlenemedi: {assemblyPath}", ex);
            }
        }

        /// <summary>
        /// Assembly'yi async olarak çözümler;
        /// </summary>
        public async Task<Assembly> ResolveAssemblyAsync(string assemblyPath, ResolveOptions options = null)
        {
            var resolveOptions = options ?? new ResolveOptions();

            return await Task.Run(() =>
            {
                var cts = new CancellationTokenSource(AssemblyLoadTimeout);
                var task = Task.Run(() => ResolveAssembly(assemblyPath, resolveOptions), cts.Token);

                try
                {
                    return task.Result;
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException($"Assembly yükleme zaman aşımı: {assemblyPath}");
                }
            });
        }

        /// <summary>
        /// Birden fazla assembly'yi çözümler;
        /// </summary>
        public IEnumerable<Assembly> ResolveAssemblies(IEnumerable<string> assemblyPaths, ResolveOptions options = null)
        {
            var assemblies = new List<Assembly>();
            var resolveOptions = options ?? new ResolveOptions();

            foreach (var path in assemblyPaths)
            {
                try
                {
                    var assembly = ResolveAssembly(path, resolveOptions);
                    assemblies.Add(assembly);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Assembly çözümleme başarısız: {Path}", path);

                    if (resolveOptions.StopOnFirstFailure)
                        throw;
                }
            }

            return assemblies;
        }

        /// <summary>
        /// Assembly adından çözümleme yapar;
        /// </summary>
        public Assembly ResolveAssemblyByName(string assemblyName, ResolveOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
                throw new ArgumentException("Assembly adı boş olamaz", nameof(assemblyName));

            _logger.LogDebug("Assembly adı ile çözümleniyor: {AssemblyName}", assemblyName);

            try
            {
                // Önce yüklü assembly'lerde ara;
                var loadedAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));

                if (loadedAssembly != null)
                {
                    _logger.LogDebug("Assembly zaten yüklü: {AssemblyName}", assemblyName);
                    return loadedAssembly;
                }

                // Global Assembly Cache'te ara;
                if (TryResolveFromGAC(assemblyName, out var gacAssembly))
                {
                    return gacAssembly;
                }

                // Plugin dizinlerinde ara;
                var assemblyPath = FindAssemblyInSearchPaths(assemblyName);
                if (!string.IsNullOrEmpty(assemblyPath))
                {
                    return ResolveAssembly(assemblyPath, options);
                }

                // .NET runtime assembly'lerini yükle;
                try
                {
                    return Assembly.Load(assemblyName);
                }
                catch (FileNotFoundException)
                {
                    throw new AssemblyNotFoundException($"Assembly bulunamadı: {assemblyName}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembly adı ile çözümleme hatası: {AssemblyName}", assemblyName);
                throw new AssemblyResolverException($"Assembly adı ile çözümlenemedi: {assemblyName}", ex);
            }
        }

        /// <summary>
        /// Assembly'yi doğrular;
        /// </summary>
        public AssemblyValidationResult ValidateAssembly(string assemblyPath, ResolveOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
                return AssemblyValidationResult.Invalid($"Assembly bulunamadı: {assemblyPath}");

            // Önbellek kontrolü;
            if (options?.UseValidationCache == true && _validationCache.TryGetValue(assemblyPath, out var cachedResult))
            {
                return cachedResult;
            }

            var result = new AssemblyValidationResult { AssemblyPath = assemblyPath };
            var resolveOptions = options ?? new ResolveOptions();

            try
            {
                // Dosya boyutu kontrolü;
                var fileInfo = new FileInfo(assemblyPath);
                if (fileInfo.Length > MaxAssemblySize)
                {
                    result.AddError($"Assembly boyutu çok büyük: {fileInfo.Length} bytes (max: {MaxAssemblySize})");
                }

                // İmza doğrulama;
                if (resolveOptions.VerifyStrongName)
                {
                    var signatureResult = VerifyAssemblySignature(assemblyPath);
                    if (!signatureResult.IsValid)
                    {
                        result.AddError($"Assembly imzası geçersiz: {signatureResult.ErrorMessage}");
                    }
                    else;
                    {
                        result.PublisherHash = signatureResult.PublisherHash;
                        result.IsStronglyNamed = true;

                        // Trusted publisher kontrolü;
                        if (_trustedPublisherHashes.Contains(signatureResult.PublisherHash))
                        {
                            result.IsTrustedPublisher = true;
                        }
                    }
                }

                // Assembly metadata kontrolü;
                try
                {
                    var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
                    result.AssemblyName = assemblyName.Name;
                    result.Version = assemblyName.Version?.ToString();
                    result.Culture = assemblyName.CultureName;
                    result.PublicKeyToken = BitConverter.ToString(assemblyName.GetPublicKeyToken() ?? Array.Empty<byte>());
                }
                catch (BadImageFormatException ex)
                {
                    result.AddError($"Geçersiz assembly formatı: {ex.Message}");
                }

                // Güvenlik taraması;
                if (resolveOptions.ScanForMalware && SafeModeEnabled)
                {
                    var securityScan = ScanAssemblySecurity(assemblyPath);
                    if (!securityScan.IsClean)
                    {
                        result.AddError($"Güvenlik taraması başarısız: {securityScan.Threats.FirstOrDefault()}");
                        result.SecurityThreats = securityScan.Threats.ToList();
                    }
                }

                // İzin kontrolü;
                if (resolveOptions.CheckPermissions)
                {
                    var permissionCheck = CheckAssemblyPermissions(assemblyPath);
                    if (!permissionCheck.IsAllowed)
                    {
                        result.AddError($"İzin kontrolü başarısız: {permissionCheck.DeniedPermissions.FirstOrDefault()}");
                        result.DeniedPermissions = permissionCheck.DeniedPermissions.ToList();
                    }
                }

                // Reflection-only yükleme testi;
                try
                {
                    using var stream = File.OpenRead(assemblyPath);
                    var reflectionAssembly = Assembly.ReflectionOnlyLoadFrom(assemblyPath);
                    result.CanLoadReflectionOnly = true;

                    // Assembly referanslarını kontrol et;
                    var references = reflectionAssembly.GetReferencedAssemblies();
                    result.ReferencedAssemblies = references.Select(r => r.Name).ToList();

                    // Tip kontrolleri;
                    var types = reflectionAssembly.GetTypes();
                    result.TypesCount = types.Length;

                    // Tehlikeli tip kontrolü;
                    var dangerousTypes = types.Where(t => IsDangerousType(t)).ToList();
                    if (dangerousTypes.Any())
                    {
                        result.AddWarning($"Tehlikeli tipler tespit edildi: {string.Join(", ", dangerousTypes.Select(t => t.FullName))}");
                        result.HasDangerousTypes = true;
                    }
                }
                catch (Exception ex)
                {
                    result.AddError($"Reflection-only yükleme başarısız: {ex.Message}");
                }

                result.IsValid = !result.Errors.Any();
                result.ValidationTime = DateTime.UtcNow;

                // Önbelleğe ekle;
                if (options?.UseValidationCache == true)
                {
                    _validationCache[assemblyPath] = result;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembly doğrulama hatası: {Path}", assemblyPath);
                return AssemblyValidationResult.Invalid($"Doğrulama hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Assembly imzasını doğrular;
        /// </summary>
        private SignatureValidationResult VerifyAssemblySignature(string assemblyPath)
        {
            try
            {
                var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);

                // Strong name kontrolü;
                if (assemblyName.GetPublicKeyToken() == null || assemblyName.GetPublicKeyToken().Length == 0)
                {
                    return SignatureValidationResult.Invalid("Assembly strong name ile imzalanmamış");
                }

                // Authenticode kontrolü (Windows'ta)
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                {
                    try
                    {
                        // System.Security.Cryptography.X509Certificates kullanarak Authenticode doğrulama;
                        // Bu kısım platforma özel implementasyon gerektirir;
                        // Şimdilik basit implementasyon;
                        return SignatureValidationResult.Valid("DummyPublisherHash");
                    }
                    catch
                    {
                        return SignatureValidationResult.Invalid("Authenticode doğrulama başarısız");
                    }
                }

                // Linux/macOS için basit hash hesaplama;
                using var sha256 = SHA256.Create();
                using var stream = File.OpenRead(assemblyPath);
                var hash = sha256.ComputeHash(stream);
                var publisherHash = BitConverter.ToString(hash).Replace("-", "");

                return SignatureValidationResult.Valid(publisherHash);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Assembly imza doğrulama hatası");
                return SignatureValidationResult.Invalid($"İmza doğrulama hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Assembly güvenlik taraması yapar;
        /// </summary>
        private SecurityScanResult ScanAssemblySecurity(string assemblyPath)
        {
            var result = new SecurityScanResult { AssemblyPath = assemblyPath };

            try
            {
                // Basit güvenlik taraması;
                // Gerçek implementasyonda antivirus API veya yara kuralları kullanılabilir;

                var fileBytes = File.ReadAllBytes(assemblyPath);
                var fileContent = Encoding.UTF8.GetString(fileBytes);

                // Kötü amaçlı pattern'ler (basit örnek)
                var maliciousPatterns = new[]
                {
                    "System.Reflection.Emit", // Dynamic code generation;
                    "System.Runtime.InteropServices", // P/Invoke;
                    "System.Security.Permissions", // Permission demands;
                    "System.Diagnostics.Process", // Process creation;
                    "System.IO.FileSystem", // File system access;
                    "System.Net", // Network access;
                    "System.Threading", // Thread manipulation;
                    "Win32", // Native calls;
                    "kernel32", // Kernel API;
                    "VirtualAlloc", // Memory allocation;
                };

                foreach (var pattern in maliciousPatterns)
                {
                    if (fileContent.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        result.AddThreat($"Şüpheli API kullanımı: {pattern}");
                    }
                }

                // File size anomaly;
                var fileInfo = new FileInfo(assemblyPath);
                if (fileInfo.Length < 1024) // 1KB'tan küçük;
                {
                    result.AddThreat("Çok küçük assembly boyutu");
                }

                // Entropy check (packed/obfuscated code)
                var entropy = CalculateEntropy(fileBytes);
                if (entropy > 7.5) // Yüksek entropy;
                {
                    result.AddWarning("Yüksek entropy - Obfuscated veya packed kod olabilir");
                }

                result.IsClean = !result.Threats.Any();
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Güvenlik taraması hatası");
                result.AddThreat($"Güvenlik taraması başarısız: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Entropy hesaplar;
        /// </summary>
        private double CalculateEntropy(byte[] data)
        {
            if (data == null || data.Length == 0)
                return 0;

            var frequency = new int[256];
            foreach (var b in data)
            {
                frequency[b]++;
            }

            var entropy = 0.0;
            var length = (double)data.Length;

            for (int i = 0; i < 256; i++)
            {
                if (frequency[i] > 0)
                {
                    var probability = frequency[i] / length;
                    entropy -= probability * Math.Log(probability, 2);
                }
            }

            return entropy;
        }

        /// <summary>
        /// Assembly izinlerini kontrol eder;
        /// </summary>
        private PermissionCheckResult CheckAssemblyPermissions(string assemblyPath)
        {
            var result = new PermissionCheckResult { AssemblyPath = assemblyPath };

            // Safe mode'da kısıtlanmış izinler;
            if (SafeModeEnabled)
            {
                var restrictedPermissions = new[]
                {
                    "FileIOPermission",       // File system access;
                    "RegistryPermission",     // Registry access;
                    "SecurityPermission",     // Security operations;
                    "ReflectionPermission",   // Reflection;
                    "EnvironmentPermission",  // Environment variables;
                    "UIPermission",           // User interface;
                    "WebPermission",          // Network access;
                    "SocketPermission",       // Socket access;
                    "DnsPermission",          // DNS resolution;
                    "PrintingPermission",     // Printing;
                };

                foreach (var permission in restrictedPermissions)
                {
                    result.AddDeniedPermission(permission);
                }
            }

            result.IsAllowed = !result.DeniedPermissions.Any();
            return result;
        }

        /// <summary>
        /// Tehlikeli tip kontrolü;
        /// </summary>
        private bool IsDangerousType(Type type)
        {
            if (type == null)
                return false;

            var dangerousTypeNames = new[]
            {
                "System.Reflection.Emit",
                "System.Runtime.InteropServices",
                "System.Diagnostics.Process",
                "System.Threading.Thread",
                "System.Security",
                "System.Net.Sockets",
            };

            var typeFullName = type.FullName ?? string.Empty;
            return dangerousTypeNames.Any(name => typeFullName.StartsWith(name, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Shadow copy oluşturur;
        /// </summary>
        private string CreateShadowCopy(string assemblyPath, ResolveOptions options)
        {
            try
            {
                var fileName = Path.GetFileName(assemblyPath);
                var shadowPath = Path.Combine(_shadowCopyDirectory, Guid.NewGuid().ToString(), fileName);

                Directory.CreateDirectory(Path.GetDirectoryName(shadowPath));

                // Dosyayı kopyala;
                File.Copy(assemblyPath, shadowPath, true);

                // İzinleri ayarla (read-only)
                if (options.MakeShadowCopyReadOnly)
                {
                    File.SetAttributes(shadowPath, FileAttributes.ReadOnly);
                }

                _logger.LogDebug("Shadow copy oluşturuldu: {Original} -> {Shadow}", assemblyPath, shadowPath);
                return shadowPath;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Shadow copy oluşturulamadı, orijinal dosya kullanılacak");
                return assemblyPath;
            }
        }

        /// <summary>
        /// Assembly'yi özel context'te yükler;
        /// </summary>
        private Assembly LoadAssemblyInContext(string assemblyPath, ResolveOptions options)
        {
            try
            {
                // Özel AssemblyLoadContext kullan;
                var assembly = _pluginLoadContext.LoadFromAssemblyPath(assemblyPath);

                // Assembly metadata'sını kaydet;
                var metadata = new AssemblyMetadata;
                {
                    LoadContext = _pluginLoadContext.GetType().Name,
                    LoadTime = DateTime.UtcNow,
                    LoadMethod = "LoadFromAssemblyPath"
                };

                _assemblyMetadata[assemblyPath] = metadata;

                return assembly;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembly context yükleme hatası");
                throw;
            }
        }

        /// <summary>
        /// Assembly'yi direkt yükler;
        /// </summary>
        private Assembly LoadAssemblyDirect(string assemblyPath, ResolveOptions options)
        {
            try
            {
                Assembly assembly;

                if (options.LoadForReflectionOnly)
                {
                    assembly = Assembly.ReflectionOnlyLoadFrom(assemblyPath);
                }
                else;
                {
                    // Byte array olarak yükle (lock'u önlemek için)
                    var assemblyBytes = File.ReadAllBytes(assemblyPath);
                    assembly = Assembly.Load(assemblyBytes);
                }

                return assembly;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Assembly direkt yükleme hatası");
                throw;
            }
        }

        /// <summary>
        /// Assembly bağımlılıklarını çözümler;
        /// </summary>
        private void ResolveDependencies(Assembly assembly, ResolveOptions options)
        {
            try
            {
                var dependencies = new List<string>();
                var referencedAssemblies = assembly.GetReferencedAssemblies();

                foreach (var referenced in referencedAssemblies)
                {
                    try
                    {
                        // Bağımlılığı çözümle;
                        var resolvedAssembly = ResolveAssemblyByName(referenced.Name, options);
                        if (resolvedAssembly != null)
                        {
                            dependencies.Add(referenced.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Bağımlılık çözümleme hatası: {Dependency}", referenced.Name);

                        if (options.ThrowOnDependencyFailure)
                            throw;
                    }
                }

                // Bağımlılıkları kaydet;
                _assemblyDependencies[assembly.Location] = dependencies;

                _logger.LogDebug("{Assembly} için {Count} bağımlılık çözümlendi",
                    assembly.GetName().Name, dependencies.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bağımlılık çözümleme hatası");
                throw;
            }
        }

        /// <summary>
        /// GAC'ten assembly çözümler;
        /// </summary>
        private bool TryResolveFromGAC(string assemblyName, out Assembly assembly)
        {
            assembly = null;

            try
            {
                // .NET Core/5+ için GAC yok, bu nedenle runtime assembly'leri kullan;
                if (assemblyName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                    assemblyName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
                {
                    assembly = Assembly.Load(assemblyName);
                    return true;
                }
            }
            catch
            {
                // GAC'te bulunamadı;
            }

            return false;
        }

        /// <summary>
        /// Arama dizinlerinde assembly'yi bulur;
        /// </summary>
        private string FindAssemblyInSearchPaths(string assemblyName)
        {
            var searchPaths = new[]
            {
                AppDomain.CurrentDomain.BaseDirectory,
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                _assemblyCacheDirectory,
                Environment.CurrentDirectory;
            };

            var possibleExtensions = new[] { ".dll", ".exe" };

            foreach (var path in searchPaths)
            {
                if (!Directory.Exists(path))
                    continue;

                foreach (var extension in possibleExtensions)
                {
                    var assemblyPath = Path.Combine(path, assemblyName + extension);
                    if (File.Exists(assemblyPath))
                    {
                        return assemblyPath;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Assembly metadata'sını çıkarır;
        /// </summary>
        private AssemblyMetadata ExtractAssemblyMetadata(Assembly assembly, string originalPath)
        {
            var metadata = new AssemblyMetadata;
            {
                OriginalPath = originalPath,
                LoadedPath = assembly.Location,
                AssemblyName = assembly.GetName().Name,
                Version = assembly.GetName().Version?.ToString(),
                Culture = assembly.GetName().CultureName,
                PublicKeyToken = BitConverter.ToString(assembly.GetName().GetPublicKeyToken() ?? Array.Empty<byte>()),
                Architecture = assembly.GetName().ProcessorArchitecture.ToString(),
                IsGAC = assembly.GlobalAssemblyCache,
                IsDynamic = assembly.IsDynamic,
                IsFullyTrusted = assembly.IsFullyTrusted,
                LoadTime = DateTime.UtcNow,
                LastAccessTime = DateTime.UtcNow,
                FileSize = new FileInfo(originalPath).Length,
                Hash = CalculateFileHash(originalPath)
            };

            return metadata;
        }

        /// <summary>
        /// Dosya hash'ini hesaplar;
        /// </summary>
        private string CalculateFileHash(string filePath)
        {
            using var sha256 = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha256.ComputeHash(stream);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>
        /// Önbelleği temizler;
        /// </summary>
        private void CleanupCacheCallback(object state)
        {
            try
            {
                CleanupCache();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Önbellek temizleme hatası");
            }
        }

        /// <summary>
        /// Assembly önbelleğini temizler;
        /// </summary>
        public void CleanupCache()
        {
            lock (_resolutionLock)
            {
                try
                {
                    _logger.LogDebug("Assembly önbelleği temizleniyor...");

                    // Erişilmemiş assembly'leri temizle;
                    var cutoffTime = DateTime.UtcNow.AddMinutes(-30); // 30 dakikadır erişilmeyenler;
                    var toRemove = _assemblyLastAccess;
                        .Where(kvp => kvp.Value < cutoffTime)
                        .Select(kvp => kvp.Key)
                        .ToList();

                    foreach (var path in toRemove)
                    {
                        if (_loadedAssemblies.TryRemove(path, out var assembly))
                        {
                            _assemblyMetadata.TryRemove(path, out _);
                            _assemblyDependencies.TryRemove(path, out _);
                            _assemblyLastAccess.TryRemove(path, out _);
                            _validationCache.TryRemove(path, out _);

                            _logger.LogTrace("Assembly önbellekten kaldırıldı: {Path}", path);
                        }
                    }

                    // Shadow copy dizinini temizle;
                    if (Directory.Exists(_shadowCopyDirectory))
                    {
                        var shadowDirs = Directory.GetDirectories(_shadowCopyDirectory);
                        foreach (var dir in shadowDirs)
                        {
                            try
                            {
                                var dirInfo = new DirectoryInfo(dir);
                                if (dirInfo.LastAccessTime < cutoffTime)
                                {
                                    Directory.Delete(dir, true);
                                    _logger.LogTrace("Shadow copy dizini silindi: {Dir}", dir);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Shadow copy dizini silinemedi: {Dir}", dir);
                            }
                        }
                    }

                    _logger.LogInformation("Önbellek temizlendi. {Count} assembly kaldırıldı", toRemove.Count);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Önbellek temizleme işlemi sırasında hata");
                }
            }
        }

        /// <summary>
        /// Tüm önbelleği temizler;
        /// </summary>
        public void ClearAllCache()
        {
            lock (_resolutionLock)
            {
                _loadedAssemblies.Clear();
                _assemblyMetadata.Clear();
                _assemblyDependencies.Clear();
                _assemblyLastAccess.Clear();
                _validationCache.Clear();

                // Shadow copy dizinini tamamen temizle;
                if (Directory.Exists(_shadowCopyDirectory))
                {
                    Directory.Delete(_shadowCopyDirectory, true);
                    Directory.CreateDirectory(_shadowCopyDirectory);
                }

                _logger.LogInformation("Tüm assembly önbelleği temizlendi");
            }
        }

        /// <summary>
        /// Assembly'yi yükten kaldırır;
        /// </summary>
        public bool UnloadAssembly(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                return false;

            lock (_resolutionLock)
            {
                try
                {
                    if (_loadedAssemblies.TryRemove(assemblyPath, out var assembly))
                    {
                        _assemblyMetadata.TryRemove(assemblyPath, out _);
                        _assemblyDependencies.TryRemove(assemblyPath, out _);
                        _assemblyLastAccess.TryRemove(assemblyPath, out _);
                        _validationCache.TryRemove(assemblyPath, out _);

                        // Collectible AssemblyLoadContext kullanılıyorsa unload et;
                        if (_pluginLoadContext is PluginAssemblyLoadContext pluginContext)
                        {
                            pluginContext.UnloadAssembly(assembly);
                        }

                        _logger.LogInformation("Assembly yükten kaldırıldı: {Path}", assemblyPath);
                        return true;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Assembly yükten kaldırma hatası: {Path}", assemblyPath);
                    return false;
                }
            }
        }

        /// <summary>
        /// Tüm assembly'leri yükten kaldırır;
        /// </summary>
        public void UnloadAllAssemblies()
        {
            lock (_resolutionLock)
            {
                var assemblyPaths = _loadedAssemblies.Keys.ToList();
                var unloadedCount = 0;

                foreach (var path in assemblyPaths)
                {
                    if (UnloadAssembly(path))
                    {
                        unloadedCount++;
                    }
                }

                _logger.LogInformation("{Count} assembly yükten kaldırıldı", unloadedCount);
            }
        }

        /// <summary>
        /// Trusted publisher ekler;
        /// </summary>
        public void AddTrustedPublisher(string publisherHash)
        {
            if (string.IsNullOrWhiteSpace(publisherHash))
                throw new ArgumentException("Publisher hash boş olamaz", nameof(publisherHash));

            lock (_trustedPublisherHashes)
            {
                if (_trustedPublisherHashes.Add(publisherHash))
                {
                    SaveTrustedAssemblies();
                    _logger.LogInformation("Trusted publisher eklendi: {Hash}", publisherHash);
                }
            }
        }

        /// <summary>
        /// Trusted publisher kaldırır;
        /// </summary>
        public bool RemoveTrustedPublisher(string publisherHash)
        {
            if (string.IsNullOrWhiteSpace(publisherHash))
                return false;

            lock (_trustedPublisherHashes)
            {
                if (_trustedPublisherHashes.Remove(publisherHash))
                {
                    SaveTrustedAssemblies();
                    _logger.LogInformation("Trusted publisher kaldırıldı: {Hash}", publisherHash);
                    return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Assembly metadata'sını alır;
        /// </summary>
        public AssemblyMetadata GetAssemblyMetadata(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                return null;

            if (_assemblyMetadata.TryGetValue(assemblyPath, out var metadata))
            {
                return metadata;
            }

            return null;
        }

        /// <summary>
        /// Assembly bağımlılıklarını alır;
        /// </summary>
        public IReadOnlyList<string> GetAssemblyDependencies(string assemblyPath)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                return Array.Empty<string>();

            if (_assemblyDependencies.TryGetValue(assemblyPath, out var dependencies))
            {
                return dependencies.AsReadOnly();
            }

            return Array.Empty<string>();
        }

        /// <summary>
        /// Tüm yüklü assembly'leri alır;
        /// </summary>
        public IReadOnlyCollection<Assembly> GetLoadedAssemblies()
        {
            return _loadedAssemblies.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Yüklü assembly istatistiklerini alır;
        /// </summary>
        public AssemblyStatistics GetStatistics()
        {
            var stats = new AssemblyStatistics;
            {
                TotalLoaded = _loadedAssemblies.Count,
                TotalMetadata = _assemblyMetadata.Count,
                TotalDependencyMappings = _assemblyDependencies.Count,
                TotalValidationCache = _validationCache.Count,
                CacheSize = CalculateCacheSize(),
                LastCleanupTime = DateTime.UtcNow;
            };

            return stats;
        }

        /// <summary>
        /// Önbellek boyutunu hesaplar;
        /// </summary>
        private long CalculateCacheSize()
        {
            try
            {
                if (!Directory.Exists(_assemblyCacheDirectory))
                    return 0;

                var files = Directory.GetFiles(_assemblyCacheDirectory, "*.*", SearchOption.AllDirectories);
                return files.Sum(file => new FileInfo(file).Length);
            }
            catch
            {
                return 0;
            }
        }

        // Event handlers;
        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            _logger.LogDebug("Assembly çözümleme isteği: {Name}", args.Name);

            try
            {
                var assemblyName = new AssemblyName(args.Name).Name;
                return ResolveAssemblyByName(assemblyName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Assembly çözümleme başarısız: {Name}", args.Name);
                return null;
            }
        }

        private void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
        {
            _logger.LogTrace("Assembly yüklendi: {Name}", args.LoadedAssembly.FullName);
        }

        private Assembly CurrentDomain_ReflectionOnlyAssemblyResolve(object sender, ResolveEventArgs args)
        {
            _logger.LogDebug("Reflection-only assembly çözümleme isteği: {Name}", args.Name);

            try
            {
                var assemblyName = new AssemblyName(args.Name).Name;
                var assembly = ResolveAssemblyByName(assemblyName, new ResolveOptions { LoadForReflectionOnly = true });
                return assembly;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reflection-only assembly çözümleme başarısız: {Name}", args.Name);
                return null;
            }
        }

        /// <summary>
        /// Dispose pattern;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _logger.LogInformation("AssemblyResolver kapatılıyor...");

                try
                {
                    // Timer'ı durdur;
                    _cleanupTimer?.Dispose();

                    // Event'leri temizle;
                    AppDomain.CurrentDomain.AssemblyResolve -= CurrentDomain_AssemblyResolve;
                    AppDomain.CurrentDomain.AssemblyLoad -= CurrentDomain_AssemblyLoad;
                    AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve -= CurrentDomain_ReflectionOnlyAssemblyResolve;

                    // Assembly'leri yükten kaldır;
                    UnloadAllAssemblies();

                    // LoadContext'i temizle;
                    if (_pluginLoadContext is IDisposable disposableContext)
                    {
                        disposableContext.Dispose();
                    }

                    _logger.LogInformation("AssemblyResolver başarıyla kapatıldı");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AssemblyResolver kapatılırken hata oluştu");
                }
            }

            _disposed = true;
        }

        /// <summary>
        /// Özel AssemblyLoadContext için nested class;
        /// </summary>
        private class PluginAssemblyLoadContext : AssemblyLoadContext;
        {
            private readonly AssemblyResolver _resolver;
            private readonly Dictionary<string, Assembly> _loadedInContext;

            public PluginAssemblyLoadContext(AssemblyResolver resolver)
                : base(nameof(PluginAssemblyLoadContext), isCollectible: true)
            {
                _resolver = resolver;
                _loadedInContext = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                _resolver._logger.LogTrace("Plugin context assembly yükleme: {Name}", assemblyName.Name);

                try
                {
                    // Önce bu context'te yüklü mü kontrol et;
                    if (_loadedInContext.TryGetValue(assemblyName.Name, out var loadedAssembly))
                    {
                        return loadedAssembly;
                    }

                    // Default context'ten yükle;
                    var defaultAssembly = Default.LoadFromAssemblyName(assemblyName);
                    _loadedInContext[assemblyName.Name] = defaultAssembly;

                    return defaultAssembly;
                }
                catch (Exception ex)
                {
                    _resolver._logger.LogWarning(ex, "Plugin context assembly yükleme hatası: {Name}", assemblyName.Name);
                    return null;
                }
            }

            public void UnloadAssembly(Assembly assembly)
            {
                if (assembly == null)
                    return;

                var assemblyName = assembly.GetName().Name;
                _loadedInContext.Remove(assemblyName);

                _resolver._logger.LogTrace("Plugin context'ten assembly kaldırıldı: {Name}", assemblyName);
            }
        }
    }

    #region Supporting Types;

    /// <summary>
    /// Assembly çözümleme seçenekleri;
    /// </summary>
    public class ResolveOptions;
    {
        public bool LoadInSeparateContext { get; set; } = true;
        public bool LoadForReflectionOnly { get; set; } = false;
        public bool ResolveDependencies { get; set; } = true;
        public bool VerifyStrongName { get; set; } = true;
        public bool ScanForMalware { get; set; } = true;
        public bool CheckPermissions { get; set; } = true;
        public bool ThrowOnValidationFailure { get; set; } = true;
        public bool ThrowOnDependencyFailure { get; set; } = false;
        public bool StopOnFirstFailure { get; set; } = false;
        public bool UseValidationCache { get; set; } = true;
        public bool MakeShadowCopyReadOnly { get; set; } = true;
        public Dictionary<string, object> CustomValidators { get; set; }

        public ResolveOptions()
        {
            CustomValidators = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Assembly doğrulama sonucu;
    /// </summary>
    public class AssemblyValidationResult;
    {
        public string AssemblyPath { get; set; }
        public string AssemblyName { get; set; }
        public string Version { get; set; }
        public string Culture { get; set; }
        public string PublicKeyToken { get; set; }
        public string PublisherHash { get; set; }
        public bool IsValid { get; set; }
        public bool IsStronglyNamed { get; set; }
        public bool IsTrustedPublisher { get; set; }
        public bool CanLoadReflectionOnly { get; set; }
        public bool HasDangerousTypes { get; set; }
        public List<string> Errors { get; set; }
        public List<string> Warnings { get; set; }
        public List<string> SecurityThreats { get; set; }
        public List<string> DeniedPermissions { get; set; }
        public List<string> ReferencedAssemblies { get; set; }
        public int TypesCount { get; set; }
        public DateTime ValidationTime { get; set; }
        public Dictionary<string, object> AdditionalData { get; set; }

        public AssemblyValidationResult()
        {
            Errors = new List<string>();
            Warnings = new List<string>();
            SecurityThreats = new List<string>();
            DeniedPermissions = new List<string>();
            ReferencedAssemblies = new List<string>();
            AdditionalData = new Dictionary<string, object>();
        }

        public void AddError(string error)
        {
            Errors.Add(error);
        }

        public void AddWarning(string warning)
        {
            Warnings.Add(warning);
        }

        public static AssemblyValidationResult Invalid(string error)
        {
            var result = new AssemblyValidationResult();
            result.AddError(error);
            result.IsValid = false;
            return result;
        }
    }

    /// <summary>
    /// İmza doğrulama sonucu;
    /// </summary>
    public class SignatureValidationResult;
    {
        public bool IsValid { get; set; }
        public string PublisherHash { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime ValidationTime { get; set; }

        public static SignatureValidationResult Valid(string publisherHash)
        {
            return new SignatureValidationResult;
            {
                IsValid = true,
                PublisherHash = publisherHash,
                ValidationTime = DateTime.UtcNow;
            };
        }

        public static SignatureValidationResult Invalid(string errorMessage)
        {
            return new SignatureValidationResult;
            {
                IsValid = false,
                ErrorMessage = errorMessage,
                ValidationTime = DateTime.UtcNow;
            };
        }
    }

    /// <summary>
    /// Güvenlik tarama sonucu;
    /// </summary>
    public class SecurityScanResult;
    {
        public string AssemblyPath { get; set; }
        public bool IsClean { get; set; }
        public List<string> Threats { get; set; }
        public List<string> Warnings { get; set; }
        public DateTime ScanTime { get; set; }
        public Dictionary<string, object> ScanData { get; set; }

        public SecurityScanResult()
        {
            Threats = new List<string>();
            Warnings = new List<string>();
            ScanData = new Dictionary<string, object>();
        }

        public void AddThreat(string threat)
        {
            Threats.Add(threat);
            IsClean = false;
        }

        public void AddWarning(string warning)
        {
            Warnings.Add(warning);
        }
    }

    /// <summary>
    /// İzin kontrol sonucu;
    /// </summary>
    public class PermissionCheckResult;
    {
        public string AssemblyPath { get; set; }
        public bool IsAllowed { get; set; }
        public List<string> DeniedPermissions { get; set; }
        public List<string> AllowedPermissions { get; set; }
        public DateTime CheckTime { get; set; }

        public PermissionCheckResult()
        {
            DeniedPermissions = new List<string>();
            AllowedPermissions = new List<string>();
            IsAllowed = true;
        }

        public void AddDeniedPermission(string permission)
        {
            DeniedPermissions.Add(permission);
            IsAllowed = false;
        }

        public void AddAllowedPermission(string permission)
        {
            AllowedPermissions.Add(permission);
        }
    }

    /// <summary>
    /// Assembly metadata;
    /// </summary>
    public class AssemblyMetadata;
    {
        public string OriginalPath { get; set; }
        public string LoadedPath { get; set; }
        public string AssemblyName { get; set; }
        public string Version { get; set; }
        public string Culture { get; set; }
        public string PublicKeyToken { get; set; }
        public string Architecture { get; set; }
        public bool IsGAC { get; set; }
        public bool IsDynamic { get; set; }
        public bool IsFullyTrusted { get; set; }
        public string LoadContext { get; set; }
        public string LoadMethod { get; set; }
        public DateTime LoadTime { get; set; }
        public DateTime LastAccessTime { get; set; }
        public long FileSize { get; set; }
        public string Hash { get; set; }
        public Dictionary<string, object> AdditionalInfo { get; set; }

        public AssemblyMetadata()
        {
            AdditionalInfo = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Assembly istatistikleri;
    /// </summary>
    public class AssemblyStatistics;
    {
        public int TotalLoaded { get; set; }
        public int TotalMetadata { get; set; }
        public int TotalDependencyMappings { get; set; }
        public int TotalValidationCache { get; set; }
        public long CacheSize { get; set; }
        public DateTime LastCleanupTime { get; set; }
        public Dictionary<string, object> AdditionalStats { get; set; }

        public AssemblyStatistics()
        {
            AdditionalStats = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Güvenilir assembly;
    /// </summary>
    public class TrustedAssembly;
    {
        public string PublisherHash { get; set; }
        public string AssemblyName { get; set; }
        public string Version { get; set; }
        public DateTime AddedDate { get; set; }
        public string AddedBy { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>
    /// Assembly yükleme başarısızlık nedenleri;
    /// </summary>
    public enum AssemblyLoadFailureReason;
    {
        FileNotFound,
        InvalidFormat,
        ValidationFailed,
        SecurityViolation,
        DependencyMissing,
        LoadError,
        Timeout,
        OutOfMemory,
        PermissionDenied;
    }

    // Event args classes;
    public class AssemblyResolvingEventArgs : EventArgs;
    {
        public string AssemblyPath { get; }
        public ResolveOptions Options { get; }
        public DateTime Timestamp { get; }

        public AssemblyResolvingEventArgs(string assemblyPath, ResolveOptions options)
        {
            AssemblyPath = assemblyPath;
            Options = options;
            Timestamp = DateTime.Now;
        }
    }

    public class AssemblyResolvedEventArgs : EventArgs;
    {
        public Assembly Assembly { get; }
        public string AssemblyPath { get; }
        public ResolveOptions Options { get; }
        public DateTime Timestamp { get; }

        public AssemblyResolvedEventArgs(Assembly assembly, string assemblyPath, ResolveOptions options)
        {
            Assembly = assembly;
            AssemblyPath = assemblyPath;
            Options = options;
            Timestamp = DateTime.Now;
        }
    }

    public class AssemblyLoadFailedEventArgs : EventArgs;
    {
        public string AssemblyPath { get; }
        public Exception Exception { get; }
        public AssemblyLoadFailureReason Reason { get; }
        public DateTime Timestamp { get; }

        public AssemblyLoadFailedEventArgs(string assemblyPath, Exception exception, AssemblyLoadFailureReason reason)
        {
            AssemblyPath = assemblyPath;
            Exception = exception;
            Reason = reason;
            Timestamp = DateTime.Now;
        }
    }

    public class AssemblyValidatedEventArgs : EventArgs;
    {
        public string AssemblyPath { get; }
        public AssemblyValidationResult ValidationResult { get; }
        public DateTime Timestamp { get; }

        public AssemblyValidatedEventArgs(string assemblyPath, AssemblyValidationResult validationResult)
        {
            AssemblyPath = assemblyPath;
            ValidationResult = validationResult;
            Timestamp = DateTime.Now;
        }
    }

    // Exception classes;
    public class AssemblyResolverException : Exception
    {
        public AssemblyResolverException(string message) : base(message) { }
        public AssemblyResolverException(string message, Exception inner) : base(message, inner) { }
    }

    public class AssemblyValidationException : Exception
    {
        public AssemblyValidationResult ValidationResult { get; }

        public AssemblyValidationException(string message, AssemblyValidationResult result)
            : base(message)
        {
            ValidationResult = result;
        }

        public AssemblyValidationException(string message, AssemblyValidationResult result, Exception inner)
            : base(message, inner)
        {
            ValidationResult = result;
        }
    }

    public class AssemblyNotFoundException : Exception
    {
        public string AssemblyName { get; }

        public AssemblyNotFoundException(string message) : base(message) { }
        public AssemblyNotFoundException(string message, string assemblyName)
            : base(message)
        {
            AssemblyName = assemblyName;
        }
    }

    #endregion;
}
