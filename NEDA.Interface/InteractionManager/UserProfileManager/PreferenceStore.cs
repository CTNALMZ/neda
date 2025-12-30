using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using NEDA.Common.Utilities;
using NEDA.Logging;

namespace NEDA.Interface.InteractionManager.UserProfileManager;
{
    /// <summary>
    /// Kullanıcı tercihlerini yönetmek için gelişmiş, thread-safe depolama sistemi;
    /// Tercihleri bellekte ve kalıcı depolamada tutar, otomatik senkronizasyon sağlar;
    /// </summary>
    public class PreferenceStore : IDisposable
    {
        #region Fields and Properties;

        private readonly ConcurrentDictionary<string, PreferenceEntry> _preferences;
        private readonly ReaderWriterLockSlim _storageLock;
        private readonly string _storagePath;
        private readonly ILogger _logger;
        private readonly JsonSerializerOptions _jsonOptions;
        private Timer _autoSaveTimer;
        private bool _isDisposed;
        private bool _isDirty;

        /// <summary>
        /// Tercih değişikliği event'i;
        /// </summary>
        public event EventHandler<PreferenceChangedEventArgs> PreferenceChanged;

        /// <summary>
        /// Depolama yolu;
        /// </summary>
        public string StoragePath => _storagePath;

        /// <summary>
        Kayıtlı tercih sayısı;
        /// </summary>
        public int Count => _preferences.Count;

        /// <summary>
        /// Otomatik kaydetme aralığı (saniye)
        /// </summary>
        public int AutoSaveInterval { get; set; } = 60;

        #endregion;

        #region Constructors;

        /// <summary>
        /// Varsayılan constructor - kullanıcı profili klasörüne kaydeder;
        /// </summary>
        public PreferenceStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "NEDA",
                "Preferences",
                $"preferences_{Environment.UserName}.json"))
        {
        }

        /// <summary>
        /// Özel depolama yolu ile constructor;
        /// </summary>
        /// <param name="storagePath">Tercihlerin kaydedileceği dosya yolu</param>
        /// <exception cref="ArgumentNullException">storagePath null ise</exception>
        public PreferenceStore(string storagePath)
        {
            if (string.IsNullOrWhiteSpace(storagePath))
                throw new ArgumentNullException(nameof(storagePath));

            _storagePath = storagePath;
            _preferences = new ConcurrentDictionary<string, PreferenceEntry>(StringComparer.OrdinalIgnoreCase);
            _storageLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            _logger = LogManager.GetLogger(typeof(PreferenceStore));
            _isDirty = false;

            // JSON serileştirme ayarları;
            _jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter() }
            };

            // Depolama dizinini oluştur;
            EnsureStorageDirectory();

            // Kayıtlı tercihleri yükle;
            LoadPreferences();

            // Otomatik kaydetme timer'ını başlat;
            StartAutoSaveTimer();

            _logger.Info($"PreferenceStore initialized. Storage: {_storagePath}");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Tercih ekler veya günceller;
        /// </summary>
        /// <typeparam name="T">Tercih değeri tipi</typeparam>
        /// <param name="key">Tercih anahtarı</param>
        /// <param name="value">Tercih değeri</param>
        /// <param name="category">Tercih kategorisi</param>
        /// <param name="description">Tercih açıklaması</param>
        /// <param name="isPersistent">Kalıcı depolamada saklansın mı?</param>
        /// <returns>Başarı durumu</returns>
        public bool SetPreference<T>(string key, T value, string category = "General",
                                   string description = null, bool isPersistent = true)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key cannot be null or empty", nameof(key));

            if (string.IsNullOrWhiteSpace(category))
                category = "General";

            var oldValue = GetPreference<T>(key, default);
            var entry = new PreferenceEntry
            {
                Key = key,
                Value = value,
                ValueType = typeof(T).FullName,
                Category = category,
                Description = description,
                IsPersistent = isPersistent,
                LastModified = DateTime.UtcNow,
                Version = 1;
            };

            bool added = _preferences.AddOrUpdate(key, entry, (k, old) =>
            {
                entry.Version = old.Version + 1;
                return entry
            }) != null;

            if (added)
            {
                _isDirty = true;

                // Event tetikle;
                OnPreferenceChanged(new PreferenceChangedEventArgs;
                {
                    Key = key,
                    OldValue = oldValue,
                    NewValue = value,
                    Category = category,
                    ChangeTime = DateTime.UtcNow;
                });

                _logger.Debug($"Preference set: {key} = {value} (Category: {category})");

                // Kalıcı ise hemen kaydet;
                if (isPersistent && AutoSaveInterval == 0)
                {
                    SavePreferences();
                }
            }

            return added;
        }

        /// <summary>
        /// Tercih değerini alır;
        /// </summary>
        /// <typeparam name="T">Beklenen değer tipi</typeparam>
        /// <param name="key">Tercih anahtarı</param>
        /// <param name="defaultValue">Tercih bulunamazsa dönecek varsayılan değer</param>
        /// <returns>Tercih değeri veya defaultValue</returns>
        public T GetPreference<T>(string key, T defaultValue = default)
        {
            if (string.IsNullOrWhiteSpace(key))
                return defaultValue;

            if (_preferences.TryGetValue(key, out var entry))
            {
                try
                {
                    if (entry.Value is T typedValue)
                        return typedValue;

                    // Tip dönüşümü dene;
                    if (entry.Value != null)
                    {
                        return (T)Convert.ChangeType(entry.Value, typeof(T));
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning($"Failed to convert preference value for key '{key}': {ex.Message}");
                    return defaultValue;
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Tercih var mı kontrol eder;
        /// </summary>
        public bool HasPreference(string key)
        {
            return _preferences.ContainsKey(key);
        }

        /// <summary>
        /// Tercihi siler;
        /// </summary>
        public bool RemovePreference(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return false;

            if (_preferences.TryRemove(key, out var removedEntry))
            {
                _isDirty = true;

                OnPreferenceChanged(new PreferenceChangedEventArgs;
                {
                    Key = key,
                    OldValue = removedEntry.Value,
                    NewValue = null,
                    Category = removedEntry.Category,
                    ChangeTime = DateTime.UtcNow,
                    IsRemoved = true;
                });

                _logger.Debug($"Preference removed: {key}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Kategoriye göre tercihleri getirir;
        /// </summary>
        public IReadOnlyDictionary<string, object> GetPreferencesByCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                category = "General";

            return _preferences;
                .Where(kvp => string.Equals(kvp.Value.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Value);
        }

        /// <summary>
        /// Tüm kategorileri getirir;
        /// </summary>
        public IEnumerable<string> GetAllCategories()
        {
            return _preferences.Values;
                .Select(e => e.Category)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c);
        }

        /// <summary>
        /// Tercihleri filtreler;
        /// </summary>
        public IEnumerable<KeyValuePair<string, PreferenceEntry>> SearchPreferences(
            Func<PreferenceEntry, bool> predicate)
        {
            if (predicate == null)
                throw new ArgumentNullException(nameof(predicate));

            return _preferences;
                .Where(kvp => predicate(kvp.Value))
                .OrderBy(kvp => kvp.Key);
        }

        /// <summary>
        /// Tercihleri dosyaya kaydeder;
        /// </summary>
        public void SavePreferences()
        {
            _storageLock.EnterWriteLock();
            try
            {
                if (!_isDirty)
                {
                    _logger.Debug("No changes to save");
                    return;
                }

                var preferencesToSave = _preferences;
                    .Where(kvp => kvp.Value.IsPersistent)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var serialized = JsonSerializer.Serialize(preferencesToSave, _jsonOptions);

                // Atomic write için geçici dosya kullan;
                var tempFile = _storagePath + ".tmp";
                File.WriteAllText(tempFile, serialized);

                // Asıl dosyaya taşı;
                File.Move(tempFile, _storagePath, true);

                _isDirty = false;
                _logger.Info($"Preferences saved to {_storagePath}. Count: {preferencesToSave.Count}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save preferences: {ex.Message}", ex);
                throw new PreferenceStoreException("Failed to save preferences", ex);
            }
            finally
            {
                _storageLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Tercihleri dosyadan yükler;
        /// </summary>
        public void LoadPreferences()
        {
            _storageLock.EnterWriteLock();
            try
            {
                if (!File.Exists(_storagePath))
                {
                    _logger.Debug("No preference file found, starting with empty store");
                    _preferences.Clear();
                    return;
                }

                var fileContent = File.ReadAllText(_storagePath);
                var loadedPreferences = JsonSerializer.Deserialize<Dictionary<string, PreferenceEntry>>(
                    fileContent, _jsonOptions);

                if (loadedPreferences != null)
                {
                    _preferences.Clear();
                    foreach (var kvp in loadedPreferences)
                    {
                        _preferences[kvp.Key] = kvp.Value;
                    }
                }

                _isDirty = false;
                _logger.Info($"Preferences loaded from {_storagePath}. Count: {_preferences.Count}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load preferences: {ex.Message}", ex);

                // Hata durumunda boş store ile devam et;
                _preferences.Clear();
                throw new PreferenceStoreException("Failed to load preferences", ex);
            }
            finally
            {
                _storageLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Tercihleri temizler (geçici olanlar hariç)
        /// </summary>
        public void ClearTemporaryPreferences()
        {
            var temporaryKeys = _preferences;
                .Where(kvp => !kvp.Value.IsPersistent)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in temporaryKeys)
            {
                _preferences.TryRemove(key, out _);
            }

            if (temporaryKeys.Count > 0)
            {
                _isDirty = true;
                _logger.Debug($"Cleared {temporaryKeys.Count} temporary preferences");
            }
        }

        /// <summary>
        /// Tercihleri JSON formatında export eder;
        /// </summary>
        public string ExportPreferences(bool includeTemporary = false)
        {
            _storageLock.EnterReadLock();
            try
            {
                var preferencesToExport = _preferences;
                    .Where(kvp => includeTemporary || kvp.Value.IsPersistent)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                return JsonSerializer.Serialize(preferencesToExport, _jsonOptions);
            }
            finally
            {
                _storageLock.ExitReadLock();
            }
        }

        /// <summary>
        /// JSON'dan tercihleri import eder;
        /// </summary>
        public void ImportPreferences(string json, ImportMode mode = ImportMode.Merge)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("JSON cannot be null or empty", nameof(json));

            _storageLock.EnterWriteLock();
            try
            {
                var importedPreferences = JsonSerializer.Deserialize<Dictionary<string, PreferenceEntry>>(
                    json, _jsonOptions);

                if (importedPreferences == null)
                    return;

                switch (mode)
                {
                    case ImportMode.Replace:
                        _preferences.Clear();
                        foreach (var kvp in importedPreferences)
                        {
                            _preferences[kvp.Key] = kvp.Value;
                        }
                        break;

                    case ImportMode.Merge:
                        foreach (var kvp in importedPreferences)
                        {
                            _preferences[kvp.Key] = kvp.Value;
                        }
                        break;

                    case ImportMode.MergeKeepExisting:
                        foreach (var kvp in importedPreferences)
                        {
                            _preferences.TryAdd(kvp.Key, kvp.Value);
                        }
                        break;
                }

                _isDirty = true;
                _logger.Info($"Imported {importedPreferences.Count} preferences. Mode: {mode}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to import preferences: {ex.Message}", ex);
                throw new PreferenceStoreException("Failed to import preferences", ex);
            }
            finally
            {
                _storageLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Tercih metadata'sını günceller;
        /// </summary>
        public bool UpdatePreferenceMetadata(string key, string description = null,
                                           string category = null, bool? isPersistent = null)
        {
            if (!_preferences.TryGetValue(key, out var entry))
                return false;

            if (!string.IsNullOrWhiteSpace(description))
                entry.Description = description;

            if (!string.IsNullOrWhiteSpace(category))
                entry.Category = category;

            if (isPersistent.HasValue)
                entry.IsPersistent = isPersistent.Value;

            entry.LastModified = DateTime.UtcNow;
            _isDirty = true;

            return true;
        }

        #endregion;

        #region Private Methods;

        private void EnsureStorageDirectory()
        {
            try
            {
                var directory = Path.GetDirectoryName(_storagePath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    _logger.Debug($"Created preferences directory: {directory}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create storage directory: {ex.Message}", ex);
                throw new PreferenceStoreException("Failed to create storage directory", ex);
            }
        }

        private void StartAutoSaveTimer()
        {
            if (AutoSaveInterval > 0)
            {
                _autoSaveTimer = new Timer(_ =>
                {
                    if (_isDirty)
                    {
                        try
                        {
                            SavePreferences();
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"Auto-save failed: {ex.Message}");
                        }
                    }
                }, null, AutoSaveInterval * 1000, AutoSaveInterval * 1000);

                _logger.Debug($"Auto-save timer started with {AutoSaveInterval}s interval");
            }
        }

        private void OnPreferenceChanged(PreferenceChangedEventArgs e)
        {
            PreferenceChanged?.Invoke(this, e);
        }

        #endregion;

        #region Dispose Pattern;

        /// <summary>
        /// Kaynakları serbest bırakır;
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_isDisposed)
            {
                if (disposing)
                {
                    // Son değişiklikleri kaydet;
                    if (_isDirty)
                    {
                        try
                        {
                            SavePreferences();
                        }
                        catch (Exception ex)
                        {
                            _logger.Error($"Failed to save preferences on dispose: {ex.Message}", ex);
                        }
                    }

                    // Timer'ı durdur;
                    _autoSaveTimer?.Dispose();
                    _autoSaveTimer = null;

                    // Lock'ı serbest bırak;
                    _storageLock?.Dispose();

                    _logger.Info("PreferenceStore disposed");
                }

                _isDisposed = true;
            }
        }

        ~PreferenceStore()
        {
            Dispose(false);
        }

        #endregion;

        #region Nested Classes;

        /// <summary>
        /// Tercih kaydı;
        /// </summary>
        public class PreferenceEntry
        {
            [JsonPropertyName("key")]
            public string Key { get; set; }

            [JsonPropertyName("value")]
            public object Value { get; set; }

            [JsonPropertyName("valueType")]
            public string ValueType { get; set; }

            [JsonPropertyName("category")]
            public string Category { get; set; } = "General";

            [JsonPropertyName("description")]
            public string Description { get; set; }

            [JsonPropertyName("isPersistent")]
            public bool IsPersistent { get; set; } = true;

            [JsonPropertyName("lastModified")]
            public DateTime LastModified { get; set; }

            [JsonPropertyName("version")]
            public int Version { get; set; } = 1;

            [JsonIgnore]
            public DateTime Created { get; set; } = DateTime.UtcNow;
        }

        /// <summary>
        /// Tercih değişikliği event args;
        /// </summary>
        public class PreferenceChangedEventArgs : EventArgs;
        {
            public string Key { get; set; }
            public object OldValue { get; set; }
            public object NewValue { get; set; }
            public string Category { get; set; }
            public DateTime ChangeTime { get; set; }
            public bool IsRemoved { get; set; }
        }

        /// <summary>
        /// Import modu;
        /// </summary>
        public enum ImportMode;
        {
            Replace,     // Tümünü değiştir;
            Merge,       // Birleştir (yeni değerler öncelikli)
            MergeKeepExisting // Birleştir (eski değerler öncelikli)
        }

        #endregion;
    }

    /// <summary>
    /// PreferenceStore özel exception'ı;
    /// </summary>
    public class PreferenceStoreException : Exception
    {
        public PreferenceStoreException() { }

        public PreferenceStoreException(string message) : base(message) { }

        public PreferenceStoreException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
