using NEDA.AI.ComputerVision;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NEDA.EngineIntegration.PluginSystem.PluginSDK;
{
    /// <summary>
    /// Tüm NEDA plugin'leri için temel interface;
    /// Bu interface'i implement eden her sınıf bir NEDA plugin'idir;
    /// </summary>
    public interface IPlugin : IDisposable
    {
        /// <summary>
        /// Plugin'in benzersiz kimliği (GUID formatında)
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Plugin'in görünen adı;
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Plugin'in açıklaması;
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Plugin sürümü (Semantic Versioning: 1.0.0)
        /// </summary>
        string Version { get; }

        /// <summary>
        /// Plugin yazarı;
        /// </summary>
        string Author { get; }

        /// <summary>
        /// Şirket/Organizasyon adı;
        /// </summary>
        string Company { get; }

        /// <summary>
        /// Copyright bilgisi;
        /// </summary>
        string Copyright { get; }

        /// <summary>
        /// Plugin'in ana ikon yolu;
        /// </summary>
        string IconPath { get; }

        /// <summary>
        /// Plugin tipi;
        /// </summary>
        PluginType PluginType { get; }

        /// <summary>
        /// Plugin'in mevcut durumu;
        /// </summary>
        PluginStatus Status { get; }

        /// <summary>
        /// Plugin etkinleştirilmiş mi?
        /// </summary>
        bool IsEnabled { get; }

        /// <summary>
        /// Sistem plugin'i mi? (Sistem plugin'leri devre dışı bırakılamaz)
        /// </summary>
        bool IsSystemPlugin { get; }

        /// <summary>
        /// Plugin bağımlılıkları;
        /// </summary>
        IReadOnlyList<PluginDependency> Dependencies { get; }

        /// <summary>
        /// Plugin uyumluluk bilgisi;
        /// </summary>
        PluginCompatibility Compatibility { get; }

        /// <summary>
        /// Plugin konfigürasyon verisi;
        /// </summary>
        PluginConfiguration ConfigData { get; }

        /// <summary>
        /// Plugin metadata (genişletilebilir veri)
        /// </summary>
        PluginMetadata Metadata { get; }

        /// <summary>
        /// Plugin'in başlatılması;
        /// Bu metod plugin'in temel başlatma işlemlerini içerir;
        /// </summary>
        /// <param name="context">Plugin bağlamı</param>
        /// <returns>Başlatma sonucu</returns>
        Task<PluginInitializationResult> InitializeAsync(IPluginContext context);

        /// <summary>
        /// Plugin'in etkinleştirilmesi;
        /// Plugin bu aşamada tam işlevsel hale gelir;
        /// </summary>
        /// <returns>Etkinleştirme sonucu</returns>
        Task<PluginActivationResult> ActivateAsync();

        /// <summary>
        /// Plugin'in devre dışı bırakılması;
        /// </summary>
        /// <returns>Devre dışı bırakma sonucu</returns>
        Task<PluginDeactivationResult> DeactivateAsync();

        /// <summary>
        /// Plugin'in durdurulması;
        /// </summary>
        /// <returns>Durdurma sonucu</returns>
        Task<PluginShutdownResult> ShutdownAsync();

        /// <summary>
        /// Plugin ayarlarını kaydet;
        /// </summary>
        /// <param name="settings">Kaydedilecek ayarlar</param>
        /// <returns>Kaydetme sonucu</returns>
        Task SaveSettingsAsync(IDictionary<string, object> settings);

        /// <summary>
        /// Plugin ayarlarını yükle;
        /// </summary>
        /// <returns>Ayarlar sözlüğü</returns>
        Task<IDictionary<string, object>> LoadSettingsAsync();

        /// <summary>
        /// Plugin istatistiklerini getir;
        /// </summary>
        /// <returns>İstatistik verisi</returns>
        Task<PluginStatistics> GetStatisticsAsync();

        /// <summary>
        /// Plugin sağlık durumunu kontrol et;
        /// </summary>
        /// <returns>Sağlık kontrol sonucu</returns>
        Task<PluginHealthCheckResult> CheckHealthAsync();

        /// <summary>
        /// Plugin için kullanılabilir komutları getir;
        /// </summary>
        /// <returns>Komut listesi</returns>
        Task<IReadOnlyList<PluginCommand>> GetCommandsAsync();

        /// <summary>
        /// Belirli bir komutu çalıştır;
        /// </summary>
        /// <param name="command">Çalıştırılacak komut</param>
        /// <param name="parameters">Komut parametreleri</param>
        /// <returns>Komut çalıştırma sonucu</returns>
        Task<CommandExecutionResult> ExecuteCommandAsync(PluginCommand command, IDictionary<string, object> parameters);

        /// <summary>
        /// Plugin için kullanılabilir araçları getir;
        /// </summary>
        /// <returns>Araç listesi</returns>
        Task<IReadOnlyList<PluginTool>> GetToolsAsync();

        /// <summary>
        /// Plugin event'lerini dinlemek için abone ol;
        /// </summary>
        /// <param name="eventHandler">Event handler</param>
        void SubscribeToEvents(IPluginEventHandler eventHandler);

        /// <summary>
        /// Plugin event'lerinden aboneliği kaldır;
        /// </summary>
        /// <param name="eventHandler">Event handler</param>
        void UnsubscribeFromEvents(IPluginEventHandler eventHandler);

        /// <summary>
        /// Plugin'in yeteneklerini getir;
        /// </summary>
        /// <returns>Yetenek listesi</returns>
        PluginCapabilities GetCapabilities();

        /// <summary>
        /// Plugin için kullanılabilir uzantı noktalarını getir;
        /// </summary>
        /// <returns>Uzantı noktaları listesi</returns>
        IReadOnlyList<ExtensionPoint> GetExtensionPoints();

        /// <summary>
        /// Plugin hata yönetimi;
        /// </summary>
        /// <param name="error">Hata bilgisi</param>
        /// <returns>Hata yönetim sonucu</returns>
        Task<ErrorHandlingResult> HandleErrorAsync(PluginError error);

        /// <summary>
        /// Plugin için lisans kontrolü;
        /// </summary>
        /// <returns>Lisans durumu</returns>
        Task<LicenseStatus> CheckLicenseAsync();

        /// <summary>
        /// Plugin güncellemelerini kontrol et;
        /// </summary>
        /// <returns>Güncelleme bilgisi</returns>
        Task<UpdateInfo> CheckForUpdatesAsync();

        /// <summary>
        /// Plugin'i güncelle;
        /// </summary>
        /// <param name="updatePackage">Güncelleme paketi</param>
        /// <returns>Güncelleme sonucu</returns>
        Task<UpdateResult> UpdateAsync(UpdatePackage updatePackage);
    }

    /// <summary>
    /// Plugin bağlam interface'i;
    /// Plugin'in çalışma ortamına erişim sağlar;
    /// </summary>
    public interface IPluginContext;
    {
        /// <summary>
        /// Logger instance'ı;
        /// </summary>
        ILogger Logger { get; }

        /// <summary>
        /// Host uygulama versiyonu;
        /// </summary>
        string HostVersion { get; }

        /// <summary>
        /// Çalışma dizini;
        /// </summary>
        string WorkingDirectory { get; }

        /// <summary>
        /// Plugin'in data dizini;
        /// </summary>
        string PluginDataDirectory { get; }

        /// <summary>
        /// Plugin'in config dizini;
        /// </summary>
        string PluginConfigDirectory { get; }

        /// <summary>
        /// Plugin'in log dizini;
        /// </summary>
        string PluginLogDirectory { get; }

        /// <summary>
        /// Plugin servis sağlayıcısı;
        /// </summary>
        IServiceProvider ServiceProvider { get; }

        /// <summary>
        /// Sistem ayarları;
        /// </summary>
        IDictionary<string, object> SystemSettings { get; }

        /// <summary>
        /// Event bus'a erişim;
        /// </summary>
        IEventBus EventBus { get; }

        /// <summary>
        /// Diğer plugin'lere erişim;
        /// </summary>
        IPluginRegistry PluginRegistry { get; }

        /// <summary>
        /// Resource manager;
        /// </summary>
        IResourceManager ResourceManager { get; }

        /// <summary>
        /// Güvenlik context'i;
        /// </summary>
        ISecurityContext SecurityContext { get; }

        /// <summary>
        /// Plugin için geçici dosya oluştur;
        /// </summary>
        /// <param name="extension">Dosya uzantısı</param>
        /// <returns>Dosya yolu</returns>
        string CreateTempFile(string extension = ".tmp");

        /// <summary>
        /// Plugin için geçici dizin oluştur;
        /// </summary>
        /// <returns>Dizin yolu</returns>
        string CreateTempDirectory();

        /// <summary>
        /// Host uygulamasına mesaj gönder;
        /// </summary>
        /// <param name="message">Gönderilecek mesaj</param>
        /// <returns>Gönderim sonucu</returns>
        Task SendMessageToHostAsync(PluginMessage message);

        /// <summary>
        /// UI thread'inde action çalıştır;
        /// </summary>
        /// <param name="action">Çalıştırılacak action</param>
        Task InvokeOnUIThreadAsync(Action action);

        /// <summary>
        /// Configuration değeri al;
        /// </summary>
        /// <param name="key">Configuration key</param>
        /// <param name="defaultValue">Varsayılan değer</param>
        /// <returns>Configuration değeri</returns>
        T GetConfiguration<T>(string key, T defaultValue = default);

        /// <summary>
        /// Resource dosyasını yükle;
        /// </summary>
        /// <param name="resourceName">Resource adı</param>
        /// <returns>Resource içeriği</returns>
        Task<byte[]> LoadResourceAsync(string resourceName);

        /// <summary>
        /// Localization string'i al;
        /// </summary>
        /// <param name="key">Localization key</param>
        /// <returns>Localized string</returns>
        string GetLocalizedString(string key);
    }

    /// <summary>
    /// Plugin event handler interface'i;
    /// </summary>
    public interface IPluginEventHandler;
    {
        /// <summary>
        /// Plugin event'ini işle;
        /// </summary>
        /// <param name="sender">Event'i gönderen plugin</param>
        /// <param name="eventArgs">Event argümanları</param>
        Task HandlePluginEventAsync(object sender, PluginEventArgs eventArgs);
    }

    /// <summary>
    /// Plugin bağımlılık bilgisi;
    /// </summary>
    public class PluginDependency;
    {
        /// <summary>
        /// Bağımlı olunan plugin ID'si;
        /// </summary>
        public string PluginId { get; set; }

        /// <summary>
        /// Gerekli minimum versiyon;
        /// </summary>
        public string RequiredVersion { get; set; }

        /// <summary>
        /// Opsiyonel bağımlılık mı?
        /// </summary>
        public bool IsOptional { get; set; }

        /// <summary>
        /// Bağımlılık açıklaması;
        /// </summary>
        public string Description { get; set; }
    }

    /// <summary>
    /// Plugin uyumluluk bilgisi;
    /// </summary>
    public class PluginCompatibility;
    {
        /// <summary>
        /// Desteklenen NEDA versiyonu;
        /// </summary>
        public string NedaVersion { get; set; }

        /// <summary>
        /// Minimum NEDA versiyonu;
        /// </summary>
        public string MinVersion { get; set; }

        /// <summary>
        /// Maksimum NEDA versiyonu;
        /// </summary>
        public string MaxVersion { get; set; }

        /// <summary>
        /// Test edilmiş versiyon;
        /// </summary>
        public string TestedVersion { get; set; }

        /// <summary>
        /// Uyumluluk seviyesi;
        /// </summary>
        public CompatibilityLevel CompatibilityLevel { get; set; }

        /// <summary>
        /// Ek notlar;
        /// </summary>
        public string Notes { get; set; }

        /// <summary>
        /// Desteklenen işletim sistemleri;
        /// </summary>
        public IReadOnlyList<string> SupportedOperatingSystems { get; set; }

        /// <summary>
        /// Gerekli .NET versiyonu;
        /// </summary>
        public string RequiredDotNetVersion { get; set; }

        /// <summary>
        /// Gerekli donanım gereksinimleri;
        /// </summary>
        public HardwareRequirements HardwareRequirements { get; set; }
    }

    /// <summary>
    /// Donanım gereksinimleri;
    /// </summary>
    public class HardwareRequirements;
    {
        /// <summary>
        /// Minimum RAM (MB)
        /// </summary>
        public int MinimumRamMb { get; set; }

        /// <summary>
        /// Önerilen RAM (MB)
        /// </summary>
        public int RecommendedRamMb { get; set; }

        /// <summary>
        /// Minimum CPU çekirdek sayısı;
        /// </summary>
        public int MinimumCpuCores { get; set; }

        /// <summary>
        /// Minimum GPU belleği (MB)
        /// </summary>
        public int MinimumGpuMemoryMb { get; set; }

        /// <summary>
        /// Minimum disk alanı (MB)
        /// </summary>
        public int MinimumDiskSpaceMb { get; set; }

        /// <summary>
        /// Gerekli ek donanımlar;
        /// </summary>
        public IReadOnlyList<string> RequiredHardware { get; set; }
    }

    /// <summary>
    /// Plugin konfigürasyonu;
    /// </summary>
    public class PluginConfiguration;
    {
        /// <summary>
        /// Plugin assembly yolu;
        /// </summary>
        public string AssemblyPath { get; set; }

        /// <summary>
        /// Giriş noktası (type full name)
        /// </summary>
        public string EntryPoint { get; set; }

        /// <summary>
        /// Plugin için özel ayarlar;
        /// </summary>
        public IDictionary<string, object> Settings { get; set; }

        /// <summary>
        /// Başlangıç parametreleri;
        /// </summary>
        public IDictionary<string, object> StartupParameters { get; set; }

        /// <summary>
        /// Plugin önceliği;
        /// </summary>
        public PluginPriority Priority { get; set; }

        /// <summary>
        /// Plugin için zaman aşımı süresi (ms)
        /// </summary>
        public int TimeoutMilliseconds { get; set; }

        /// <summary>
        /// Maksimum bellek kullanımı (MB)
        /// </summary>
        public int MaxMemoryUsageMb { get; set; }

        /// <summary>
        /// İzin verilen işlemler;
        /// </summary>
        public IReadOnlyList<PluginPermission> Permissions { get; set; }

        /// <summary>
        /// Güvenlik kısıtlamaları;
        /// </summary>
        public SecurityRestrictions SecurityRestrictions { get; set; }
    }

    /// <summary>
    /// Güvenlik kısıtlamaları;
    /// </summary>
    public class SecurityRestrictions;
    {
        /// <summary>
        /// İzin verilen dosya sistemleri;
        /// </summary>
        public IReadOnlyList<string> AllowedFileSystems { get; set; }

        /// <summary>
        /// İzin verilen network erişimleri;
        /// </summary>
        public IReadOnlyList<string> AllowedNetworkAccess { get; set; }

        /// <summary>
        /// İzin verilen registry anahtarları;
        /// </summary>
        public IReadOnlyList<string> AllowedRegistryKeys { get; set; }

        /// <summary>
        /// İzin verilen process'ler;
        /// </summary>
        public IReadOnlyList<string> AllowedProcesses { get; set; }

        /// <summary>
        /// Sandbox modunda çalıştır;
        /// </summary>
        public bool RunInSandbox { get; set; }

        /// <summary>
        /// Kısıtlı izinlerle çalıştır;
        /// </summary>
        public bool RunWithRestrictedPermissions { get; set; }
    }

    /// <summary>
    /// Plugin metadata;
    /// </summary>
    public class PluginMetadata;
    {
        /// <summary>
        /// Oluşturulma tarihi;
        /// </summary>
        public DateTime CreatedDate { get; set; }

        /// <summary>
        /// Güncelleme tarihi;
        /// </summary>
        public DateTime UpdatedDate { get; set; }

        /// <summary>
        /// Plugin kategorisi;
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Plugin etiketleri;
        /// </summary>
        public IReadOnlyList<string> Tags { get; set; }

        /// <summary>
        /// Plugin anahtar kelimeleri;
        /// </summary>
        public IReadOnlyList<string> Keywords { get; set; }

        /// <summary>
        /// Plugin web sitesi;
        /// </summary>
        public string Website { get; set; }

        /// <summary>
        /// Plugin dökümantasyon linki;
        /// </summary>
        public string DocumentationUrl { get; set; }

        /// <summary>
        /// Destek linki;
        /// </summary>
        public string SupportUrl { get; set; }

        /// <summary>
        /// Plugin lisansı;
        /// </summary>
        public string License { get; set; }

        /// <summary>
        /// Plugin lisans sözleşmesi;
        /// </summary>
        public string LicenseAgreement { get; set; }

        /// <summary>
        /// Gizlilik politikası;
        /// </summary>
        public string PrivacyPolicy { get; set; }

        /// <summary>
        /// Ek metadata bilgileri;
        /// </summary>
        public IDictionary<string, object> AdditionalData { get; set; }
    }

    /// <summary>
    /// Plugin başlatma sonucu;
    /// </summary>
    public class PluginInitializationResult;
    {
        /// <summary>
        /// Başarılı mı?
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Başlatma süresi (ms)
        /// </summary>
        public long InitializationTimeMs { get; set; }

        /// <summary>
        /// Başlatma sırasında yüklenen kaynaklar;
        /// </summary>
        public IReadOnlyList<string> LoadedResources { get; set; }

        /// <summary>
        /// Başlatma sırasında oluşturulan dosyalar;
        /// </summary>
        public IReadOnlyList<string> CreatedFiles { get; set; }
    }

    /// <summary>
    /// Plugin etkinleştirme sonucu;
    /// </summary>
    public class PluginActivationResult;
    {
        /// <summary>
        /// Başarılı mı?
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Etkinleştirme süresi (ms)
        /// </summary>
        public long ActivationTimeMs { get; set; }

        /// <summary>
        /// Etkinleştirilen servisler;
        /// </summary>
        public IReadOnlyList<string> ActivatedServices { get; set; }

        /// <summary>
        /// Kullanılabilir özellikler;
        /// </summary>
        public IReadOnlyList<string> AvailableFeatures { get; set; }
    }

    /// <summary>
    /// Plugin devre dışı bırakma sonucu;
    /// </summary>
    public class PluginDeactivationResult;
    {
        /// <summary>
        /// Başarılı mı?
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Devre dışı bırakma süresi (ms)
        /// </summary>
        public long DeactivationTimeMs { get; set; }

        /// <summary>
        /// Devre dışı bırakılan servisler;
        /// </summary>
        public IReadOnlyList<string> DeactivatedServices { get; set; }

        /// <summary>
        /// Serbest bırakılan kaynaklar;
        /// </summary>
        public IReadOnlyList<string> ReleasedResources { get; set; }
    }

    /// <summary>
    /// Plugin durdurma sonucu;
    /// </summary>
    public class PluginShutdownResult;
    {
        /// <summary>
        /// Başarılı mı?
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Durdurma süresi (ms)
        /// </summary>
        public long ShutdownTimeMs { get; set; }

        /// <summary>
        /// Temizlenen kaynaklar;
        /// </summary>
        public IReadOnlyList<string> CleanedResources { get; set; }

        /// <summary>
        /// Kaydedilen durumlar;
        /// </summary>
        public IReadOnlyList<string> SavedStates { get; set; }
    }

    /// <summary>
    /// Plugin istatistikleri;
    /// </summary>
    public class PluginStatistics;
    {
        /// <summary>
        /// Toplam çalışma süresi (ms)
        /// </summary>
        public long TotalUptimeMs { get; set; }

        /// <summary>
        /// Toplam yükleme sayısı;
        /// </summary>
        public int TotalLoadCount { get; set; }

        /// <summary>
        /// Son yükleme zamanı;
        /// </summary>
        public DateTime LastLoadTime { get; set; }

        /// <summary>
        /// Ortalama yükleme süresi (ms)
        /// </summary>
        public long AverageLoadTimeMs { get; set; }

        /// <summary>
        /// Toplam hata sayısı;
        /// </summary>
        public int TotalErrorCount { get; set; }

        /// <summary>
        /// Son hata mesajı;
        /// </summary>
        public string LastErrorMessage { get; set; }

        /// <summary>
        /// Son hata zamanı;
        /// </summary>
        public DateTime LastErrorTime { get; set; }

        /// <summary>
        /// Toplam bellek kullanımı (MB)
        /// </summary>
        public long TotalMemoryUsageMb { get; set; }

        /// <summary>
        /// Ortalama CPU kullanımı (%)
        /// </summary>
        public double AverageCpuUsagePercent { get; set; }

        /// <summary>
        /// Performans skoru (0-100)
        /// </summary>
        public double PerformanceScore { get; set; }

        /// <summary>
        /// Toplam işlem sayısı;
        /// </summary>
        public int TotalOperations { get; set; }

        /// <summary>
        /// Başarılı işlem sayısı;
        /// </summary>
        public int SuccessfulOperations { get; set; }

        /// <summary>
        /// Başarısız işlem sayısı;
        /// </summary>
        public int FailedOperations { get; set; }
    }

    /// <summary>
    /// Plugin sağlık kontrol sonucu;
    /// </summary>
    public class PluginHealthCheckResult;
    {
        /// <summary>
        /// Sağlıklı mı?
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// Sağlık durumu;
        /// </summary>
        public HealthStatus Status { get; set; }

        /// <summary>
        /// Detaylı sağlık bilgisi;
        /// </summary>
        public string HealthDetails { get; set; }

        /// <summary>
        /// Kritik hatalar;
        /// </summary>
        public IReadOnlyList<string> CriticalIssues { get; set; }

        /// <summary>
        /// Uyarılar;
        /// </summary>
        public IReadOnlyList<string> Warnings { get; set; }

        /// <summary>
        /// Öneriler;
        /// </summary>
        public IReadOnlyList<string> Recommendations { get; set; }

        /// <summary>
        /// Kontrol süresi (ms)
        /// </summary>
        public long CheckTimeMs { get; set; }

        /// <summary>
        /// Son kontrol zamanı;
        /// </summary>
        public DateTime LastCheckTime { get; set; }
    }

    /// <summary>
    /// Plugin komutu;
    /// </summary>
    public class PluginCommand;
    {
        /// <summary>
        /// Komut ID'si;
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Komut adı;
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Komut açıklaması;
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Komut kategorisi;
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Komut ikon yolu;
        /// </summary>
        public string IconPath { get; set; }

        /// <summary>
        /// Komut tipi;
        /// </summary>
        public CommandType CommandType { get; set; }

        /// <summary>
        /// Komut parametreleri;
        /// </summary>
        public IReadOnlyList<CommandParameter> Parameters { get; set; }

        /// <summary>
        /// Gerekli izinler;
        /// </summary>
        public IReadOnlyList<PluginPermission> RequiredPermissions { get; set; }

        /// <summary>
        /// Komut kısayolu;
        /// </summary>
        public string ShortcutKey { get; set; }

        /// <summary>
        /// Komut görünürlüğü;
        /// </summary>
        public bool IsVisible { get; set; }

        /// <summary>
        /// Komut etkin mi?
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Komut metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Komut parametresi;
    /// </summary>
    public class CommandParameter;
    {
        /// <summary>
        /// Parametre adı;
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Parametre tipi;
        /// </summary>
        public ParameterType Type { get; set; }

        /// <summary>
        /// Parametre açıklaması;
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Varsayılan değer;
        /// </summary>
        public object DefaultValue { get; set; }

        /// <summary>
        /// Zorunlu mu?
        /// </summary>
        public bool IsRequired { get; set; }

        /// <summary>
        /// Geçerli değerler;
        /// </summary>
        public IReadOnlyList<object> ValidValues { get; set; }

        /// <summary>
        /// Minimum değer (sayısal tipler için)
        /// </summary>
        public object MinValue { get; set; }

        /// <summary>
        /// Maksimum değer (sayısal tipler için)
        /// </summary>
        public object MaxValue { get; set; }

        /// <summary>
        /// Regex pattern (string tipler için)
        /// </summary>
        public string Pattern { get; set; }
    }

    /// <summary>
    /// Komut çalıştırma sonucu;
    /// </summary>
    public class CommandExecutionResult;
    {
        /// <summary>
        /// Başarılı mı?
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Çalıştırma süresi (ms)
        /// </summary>
        public long ExecutionTimeMs { get; set; }

        /// <summary>
        /// Çıktı verisi;
        /// </summary>
        public object Output { get; set; }

        /// <summary>
        /// Çıktı tipi;
        /// </summary>
        public OutputType OutputType { get; set; }

        /// <summary>
        /// İşlem ID'si;
        /// </summary>
        public string OperationId { get; set; }

        /// <summary>
        /// İşlem metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Plugin aracı;
    /// </summary>
    public class PluginTool;
    {
        /// <summary>
        /// Araç ID'si;
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Araç adı;
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Araç açıklaması;
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Araç tipi;
        /// </summary>
        public ToolType ToolType { get; set; }

        /// <summary>
        /// Araç ikon yolu;
        /// </summary>
        public string IconPath { get; set; }

        /// <summary>
        /// Araç kategorisi;
        /// </summary>
        public string Category { get; set; }

        /// <summary>
        /// Araç versiyonu;
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// Araç metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }

        /// <summary>
        /// Araç görünürlüğü;
        /// </summary>
        public bool IsVisible { get; set; }

        /// <summary>
        /// Araç etkin mi?
        /// </summary>
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// Plugin yetenekleri;
    /// </summary>
    public class PluginCapabilities;
    {
        /// <summary>
        /// Dosya sistemi erişimi;
        /// </summary>
        public bool CanAccessFileSystem { get; set; }

        /// <summary>
        /// Network erişimi;
        /// </summary>
        public bool CanAccessNetwork { get; set; }

        /// <summary>
        /// Registry erişimi;
        /// </summary>
        public bool CanAccessRegistry { get; set; }

        /// <summary>
        /// Process oluşturma;
        /// </summary>
        public bool CanCreateProcesses { get; set; }

        /// <summary>
        /// UI oluşturma;
        /// </summary>
        public bool CanCreateUI { get; set; }

        /// <summary>
        /// Database erişimi;
        /// </summary>
        public bool CanAccessDatabase { get; set; }

        /// <summary>
        /// Web servis çağrıları;
        /// </summary>
        public bool CanCallWebServices { get; set; }

        /// <summary>
        /// Multithreading;
        /// </summary>
        public bool CanUseMultithreading { get; set; }

        /// <summary>
        /// GPU erişimi;
        /// </summary>
        public bool CanAccessGPU { get; set; }

        /// <summary>
        /// Kamera erişimi;
        /// </summary>
        public bool CanAccessCamera { get; set; }

        /// <summary>
        /// Mikrofon erişimi;
        /// </summary>
        public bool CanAccessMicrophone { get; set; }

        /// <summary>
        /// Konum erişimi;
        /// </summary>
        public bool CanAccessLocation { get; set; }

        /// <summary>
        /// Bildirim gönderme;
        /// </summary>
        public bool CanSendNotifications { get; set; }

        /// <summary>
        /// Plugin yükleme;
        /// </summary>
        public bool CanLoadPlugins { get; set; }

        /// <summary>
        /// Sistem bilgisi okuma;
        /// </summary>
        public bool CanReadSystemInfo { get; set; }

        /// <summary>
        /// Güvenli depolama;
        /// </summary>
        public bool HasSecureStorage { get; set; }

        /// <summary>
        /// Şifreleme işlemleri;
        /// </summary>
        public bool CanPerformEncryption { get; set; }
    }

    /// <summary>
    /// Uzantı noktası;
    /// </summary>
    public class ExtensionPoint;
    {
        /// <summary>
        /// Uzantı noktası ID'si;
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Uzantı noktası adı;
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Uzantı noktası açıklaması;
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Uzantı noktası tipi;
        /// </summary>
        public ExtensionPointType Type { get; set; }

        /// <summary>
        /// Desteklenen interface'ler;
        /// </summary>
        public IReadOnlyList<Type> SupportedInterfaces { get; set; }

        /// <summary>
        /// Uzantı noktası metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Plugin hatası;
    /// </summary>
    public class PluginError;
    {
        /// <summary>
        /// Hata ID'si;
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// Hata kodu;
        /// </summary>
        public string ErrorCode { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// Hata tipi;
        /// </summary>
        public ErrorType ErrorType { get; set; }

        /// <summary>
        /// Hata seviyesi;
        /// </summary>
        public ErrorSeverity Severity { get; set; }

        /// <summary>
        /// Hata zamanı;
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Stack trace;
        /// </summary>
        public string StackTrace { get; set; }

        /// <summary>
        /// İç hata;
        /// </summary>
        public Exception InnerException { get; set; }
    }

    /// <summary>
    /// Hata yönetim sonucu;
    /// </summary>
    public class ErrorHandlingResult;
    {
        /// <summary>
        /// Hata işlendi mi?
        /// </summary>
        public bool IsHandled { get; set; }

        /// <summary>
        /// Hata kurtarıldı mı?
        /// </summary>
        public bool IsRecovered { get; set; }

        /// <summary>
        /// Kurtarma yöntemi;
        /// </summary>
        public RecoveryMethod RecoveryMethod { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Kurtarma detayları;
        /// </summary>
        public string RecoveryDetails { get; set; }

        /// <summary>
        /// Önerilen eylemler;
        /// </summary>
        public IReadOnlyList<string> RecommendedActions { get; set; }
    }

    /// <summary>
    /// Lisans durumu;
    /// </summary>
    public class LicenseStatus;
    {
        /// <summary>
        /// Lisanslı mı?
        /// </summary>
        public bool IsLicensed { get; set; }

        /// <summary>
        /// Lisans tipi;
        /// </summary>
        public LicenseType LicenseType { get; set; }

        /// <summary>
        /// Lisans anahtarı;
        /// </summary>
        public string LicenseKey { get; set; }

        /// <summary>
        /// Lisans sahibi;
        /// </summary>
        public string LicenseOwner { get; set; }

        /// <summary>
        /// Lisans geçerlilik tarihi;
        /// </summary>
        public DateTime ValidUntil { get; set; }

        /// <summary>
        /// Lisans kısıtlamaları;
        /// </summary>
        public LicenseRestrictions Restrictions { get; set; }

        /// <summary>
        /// Lisans doğrulama tarihi;
        /// </summary>
        public DateTime ValidationDate { get; set; }
    }

    /// <summary>
    /// Lisans kısıtlamaları;
    /// </summary>
    public class LicenseRestrictions;
    {
        /// <summary>
        /// Maksimum kullanıcı sayısı;
        /// </summary>
        public int MaxUsers { get; set; }

        /// <summary>
        /// Maksimum cihaz sayısı;
        /// </summary>
        public int MaxDevices { get; set; }

        /// <summary>
        /// Maksimum işlem sayısı;
        /// </summary>
        public int MaxOperations { get; set; }

        /// <summary>
        /// Tarih kısıtlaması;
        /// </summary>
        public DateTime ExpirationDate { get; set; }

        /// <summary>
        /// Özellik kısıtlamaları;
        /// </summary>
        public IReadOnlyList<string> FeatureRestrictions { get; set; }

        /// <summary>
        /// Kullanım kısıtlamaları;
        /// </summary>
        public IReadOnlyList<string> UsageRestrictions { get; set; }
    }

    /// <summary>
    /// Güncelleme bilgisi;
    /// </summary>
    public class UpdateInfo;
    {
        /// <summary>
        /// Güncelleme mevcut mu?
        /// </summary>
        public bool UpdateAvailable { get; set; }

        /// <summary>
        /// Mevcut versiyon;
        /// </summary>
        public string CurrentVersion { get; set; }

        /// <summary>
        /// Yeni versiyon;
        /// </summary>
        public string NewVersion { get; set; }

        /// <summary>
        /// Güncelleme boyutu (bytes)
        /// </summary>
        public long UpdateSizeBytes { get; set; }

        /// <summary>
        /// Güncelleme açıklaması;
        /// </summary>
        public string UpdateDescription { get; set; }

        /// <summary>
        /// Güncelleme notları;
        /// </summary>
        public string ReleaseNotes { get; set; }

        /// <summary>
        /// Güncelleme tarihi;
        /// </summary>
        public DateTime ReleaseDate { get; set; }

        /// <summary>
        /// Güncelleme tipi;
        /// </summary>
        public UpdateType UpdateType { get; set; }

        /// <summary>
        /// Güncelleme URL'si;
        /// </summary>
        public string UpdateUrl { get; set; }

        /// <summary>
        /// Güncelleme doğrulama hash'i;
        /// </summary>
        public string UpdateHash { get; set; }
    }

    /// <summary>
    /// Güncelleme paketi;
    /// </summary>
    public class UpdatePackage;
    {
        /// <summary>
        /// Paket ID'si;
        /// </summary>
        public string PackageId { get; set; }

        /// <summary>
        /// Paket versiyonu;
        /// </summary>
        public string Version { get; set; }

        /// <summary>
        /// Paket dosya yolu;
        /// </summary>
        public string PackagePath { get; set; }

        /// <summary>
        /// Paket hash'i;
        /// </summary>
        public string PackageHash { get; set; }

        /// <summary>
        /// Paket imzası;
        /// </summary>
        public string Signature { get; set; }

        /// <summary>
        /// Paket metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Güncelleme sonucu;
    /// </summary>
    public class UpdateResult;
    {
        /// <summary>
        /// Başarılı mı?
        /// </summary>
        public bool IsSuccess { get; set; }

        /// <summary>
        /// Hata mesajı;
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Hata detayları;
        /// </summary>
        public Exception Exception { get; set; }

        /// <summary>
        /// Güncelleme süresi (ms)
        /// </summary>
        public long UpdateTimeMs { get; set; }

        /// <summary>
        /// Önceki versiyon;
        /// </summary>
        public string PreviousVersion { get; set; }

        /// <summary>
        /// Yeni versiyon;
        /// </summary>
        public string NewVersion { get; set; }

        /// <summary>
        /// Yedekleme yolu;
        /// </summary>
        public string BackupPath { get; set; }

        /// <summary>
        /// Güncelleme log'u;
        /// </summary>
        public string UpdateLog { get; set; }
    }

    /// <summary>
    /// Plugin event argümanları;
    /// </summary>
    public class PluginEventArgs : EventArgs;
    {
        /// <summary>
        /// Event tipi;
        /// </summary>
        public PluginEventType EventType { get; set; }

        /// <summary>
        /// Event kaynağı;
        /// </summary>
        public object Source { get; set; }

        /// <summary>
        /// Event verisi;
        /// </summary>
        public object Data { get; set; }

        /// <summary>
        /// Event zamanı;
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Event metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
    }

    /// <summary>
    /// Plugin mesajı;
    /// </summary>
    public class PluginMessage;
    {
        /// <summary>
        /// Mesaj ID'si;
        /// </summary>
        public string MessageId { get; set; }

        /// <summary>
        /// Mesaj tipi;
        /// </summary>
        public MessageType MessageType { get; set; }

        /// <summary>
        /// Mesaj içeriği;
        /// </summary>
        public object Content { get; set; }

        /// <summary>
        /// Gönderen plugin ID'si;
        /// </summary>
        public string SenderPluginId { get; set; }

        /// <summary>
        /// Alıcı plugin ID'si (boşsa broadcast)
        /// </summary>
        public string RecipientPluginId { get; set; }

        /// <summary>
        /// Mesaj zamanı;
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Mesaj metadata'sı;
        /// </summary>
        public IDictionary<string, object> Metadata { get; set; }
    }

    #region Enums;

    /// <summary>
    /// Plugin tipi;
    /// </summary>
    public enum PluginType;
    {
        Core = 0,
        Integration = 1,
        Tool = 2,
        Extension = 3,
        Theme = 4,
        LanguagePack = 5,
        Asset = 6,
        Custom = 99;
    }

    /// <summary>
    /// Plugin durumu;
    /// </summary>
    public enum PluginStatus;
    {
        Installed = 0,
        Active = 1,
        Disabled = 2,
        Error = 3,
        Updating = 4,
        Uninstalling = 5;
    }

    /// <summary>
    /// Uyumluluk seviyesi;
    /// </summary>
    public enum CompatibilityLevel;
    {
        Unknown = 0,
        Incompatible = 1,
        Limited = 2,
        Compatible = 3,
        FullyCompatible = 4,
        Certified = 5;
    }

    /// <summary>
    /// Plugin önceliği;
    /// </summary>
    public enum PluginPriority;
    {
        Lowest = 0,
        Low = 1,
        Normal = 2,
        High = 3,
        Highest = 4,
        Critical = 5;
    }

    /// <summary>
    /// Plugin izni;
    /// </summary>
    public enum PluginPermission;
    {
        FileSystemRead = 0,
        FileSystemWrite = 1,
        NetworkAccess = 2,
        RegistryRead = 3,
        RegistryWrite = 4,
        ProcessCreation = 5,
        UICreation = 6,
        DatabaseAccess = 7,
        WebServiceCalls = 8,
        Multithreading = 9,
        GPUAccess = 10,
        CameraAccess = 11,
        MicrophoneAccess = 12,
        LocationAccess = 13,
        NotificationSending = 14,
        PluginLoading = 15,
        SystemInfoReading = 16;
    }

    /// <summary>
    /// Sağlık durumu;
    /// </summary>
    public enum HealthStatus;
    {
        Unknown = 0,
        Healthy = 1,
        Degraded = 2,
        Unhealthy = 3,
        Critical = 4;
    }

    /// <summary>
    /// Komut tipi;
    /// </summary>
    public enum CommandType;
    {
        Synchronous = 0,
        Asynchronous = 1,
        Background = 2,
        UI = 3,
        System = 4,
        Custom = 99;
    }

    /// <summary>
    /// Parametre tipi;
    /// </summary>
    public enum ParameterType;
    {
        String = 0,
        Integer = 1,
        Boolean = 2,
        Double = 3,
        DateTime = 4,
        File = 5,
        Directory = 6,
        Enum = 7,
        Object = 8,
        Array = 9;
    }

    /// <summary>
    /// Çıktı tipi;
    /// </summary>
    public enum OutputType;
    {
        None = 0,
        Text = 1,
        Json = 2,
        Xml = 3,
        Binary = 4,
        File = 5,
        Object = 6;
    }

    /// <summary>
    /// Araç tipi;
    /// </summary>
    public enum ToolType;
    {
        Utility = 0,
        Editor = 1,
        Viewer = 2,
        Analyzer = 3,
        Debugger = 4,
        Optimizer = 5,
        Converter = 6,
        Generator = 7,
        Validator = 8,
        Custom = 99;
    }

    /// <summary>
    /// Uzantı noktası tipi;
    /// </summary>
    public enum ExtensionPointType;
    {
        Menu = 0,
        Toolbar = 1,
        Panel = 2,
        Dialog = 3,
        Service = 4,
        Provider = 5,
        Handler = 6,
        Renderer = 7,
        Validator = 8,
        Transformer = 9,
        Custom = 99;
    }

    /// <summary>
    /// Hata tipi;
    /// </summary>
    public enum ErrorType;
    {
        Unknown = 0,
        Initialization = 1,
        Execution = 2,
        Resource = 3,
        Permission = 4,
        Network = 5,
        FileSystem = 6,
        Database = 7,
        Memory = 8,
        Timeout = 9,
        Validation = 10,
        Security = 11,
        Compatibility = 12,
        License = 13;
    }

    /// <summary>
    /// Hata şiddeti;
    /// </summary>
    public enum ErrorSeverity;
    {
        Information = 0,
        Warning = 1,
        Error = 2,
        Critical = 3;
    }

    /// <summary>
    /// Kurtarma yöntemi;
    /// </summary>
    public enum RecoveryMethod;
    {
        None = 0,
        Retry = 1,
        Fallback = 2,
        Restart = 3,
        Reset = 4,
        Skip = 5,
        Manual = 6;
    }

    /// <summary>
    /// Lisans tipi;
    /// </summary>
    public enum LicenseType;
    {
        Trial = 0,
        Free = 1,
        Personal = 2,
        Professional = 3,
        Enterprise = 4,
        Academic = 5,
        OpenSource = 6,
        Custom = 99;
    }

    /// <summary>
    /// Güncelleme tipi;
    /// </summary>
    public enum UpdateType;
    {
        Patch = 0,
        Minor = 1,
        Major = 2,
        Security = 3,
        Feature = 4,
        Critical = 5;
    }

    /// <summary>
    /// Plugin event tipi;
    /// </summary>
    public enum PluginEventType;
    {
        Initialized = 0,
        Activated = 1,
        Deactivated = 2,
        Shutdown = 3,
        Error = 4,
        Progress = 5,
        StatusChanged = 6,
        SettingsChanged = 7,
        CommandExecuted = 8,
        UpdateAvailable = 9,
        Custom = 99;
    }

    /// <summary>
    /// Mesaj tipi;
    /// </summary>
    public enum MessageType;
    {
        Information = 0,
        Warning = 1,
        Error = 2,
        Request = 3,
        Response = 4,
        Notification = 5,
        Command = 6,
        Event = 7,
        Custom = 99;
    }

    #endregion;

    #region Supporting Interfaces;

    /// <summary>
    /// Event bus interface'i;
    /// </summary>
    public interface IEventBus;
    {
        Task PublishAsync<TEvent>(TEvent @event) where TEvent : class;
        Task SubscribeAsync<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
        Task UnsubscribeAsync<TEvent>(Func<TEvent, Task> handler) where TEvent : class;
    }

    /// <summary>
    /// Plugin registry interface'i;
    /// </summary>
    public interface IPluginRegistry
    {
        Task<IPlugin> GetPluginAsync(string pluginId);
        Task<IEnumerable<IPlugin>> GetPluginsAsync();
        Task<bool> IsPluginLoadedAsync(string pluginId);
        Task<bool> IsPluginEnabledAsync(string pluginId);
    }

    /// <summary>
    /// Resource manager interface'i;
    /// </summary>
    public interface IResourceManager;
    {
        Task<byte[]> GetResourceAsync(string resourceName);
        Task<string> GetResourceStringAsync(string resourceName);
        Task<Stream> GetResourceStreamAsync(string resourceName);
        Task<bool> ResourceExistsAsync(string resourceName);
    }

    /// <summary>
    /// Güvenlik context interface'i;
    /// </summary>
    public interface ISecurityContext;
    {
        string CurrentUser { get; }
        IReadOnlyList<string> UserRoles { get; }
        IReadOnlyList<string> UserPermissions { get; }
        bool HasPermission(string permission);
        bool IsInRole(string role);
        Task<bool> AuthenticateAsync(string username, string password);
        Task<bool> AuthorizeAsync(string permission);
    }

    #endregion;
}
