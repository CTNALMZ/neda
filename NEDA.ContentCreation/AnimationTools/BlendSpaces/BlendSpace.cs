using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using NEDA.Animation;
using NEDA.Common;
using NEDA.ExceptionHandling;
using NEDA.Logging;
using NEDA.Services.Messaging.EventBus;
using NEDA.Mathematics;

namespace NEDA.Animation.AnimationTools.BlendSpaces;
{
    /// <summary>
    /// Blend Space - Animasyon blend'leme için 1D, 2D ve 3D blend alanları sağlar;
    /// </summary>
    public class BlendSpace : IBlendSpace, IDisposable;
    {
        private readonly ILogger _logger;
        private readonly IErrorReporter _errorReporter;
        private readonly IEventBus _eventBus;
        private readonly IAnimationBlender _animationBlender;
        private readonly IStateMachine _stateMachine;

        private readonly Dictionary<string, BlendSpaceDimension> _dimensions;
        private readonly Dictionary<string, AnimationSample> _samples;
        private readonly Dictionary<string, BlendSpaceParameter> _parameters;
        private readonly Dictionary<string, BlendSpaceGrid> _grids;

        private BlendSpaceType _type;
        private string _activeGridId;
        private bool _isInitialized;
        private bool _isActive;
        private float _blendThreshold = 0.01f;
        private int _maxSamples = 100;

        private AnimationSample _currentResult;
        private Vector3 _currentParameterValues;
        private List<WeightedSample> _currentWeights;

        /// <summary>
        /// Blend Space olayları;
        /// </summary>
        public event EventHandler<BlendSpaceCreatedEventArgs> BlendSpaceCreated;
        public event EventHandler<SampleAddedEventArgs> SampleAdded;
        public event EventHandler<SampleRemovedEventArgs> SampleRemoved;
        public event EventHandler<ParameterChangedEventArgs> ParameterChanged;
        public event EventHandler<BlendResultChangedEventArgs> BlendResultChanged;
        public event EventHandler<GridChangedEventArgs> GridChanged;

        /// <summary>
        /// Blend Space ID;
        /// </summary>
        public string Id { get; private set; }

        /// <summary>
        /// Blend Space adı;
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Blend Space tipi;
        /// </summary>
        public BlendSpaceType Type;
        {
            get => _type;
            private set;
            {
                if (_type != value)
                {
                    _type = value;
                    OnTypeChanged(value);
                }
            }
        }

        /// <summary>
        /// Boyut sayısı;
        /// </summary>
        public int DimensionCount => _dimensions.Count;

        /// <summary>
        /// Aktif grid;
        /// </summary>
        public BlendSpaceGrid ActiveGrid => _activeGridId != null && _grids.ContainsKey(_activeGridId)
            ? _grids[_activeGridId]
            : null;

        /// <summary>
        /// Örnek sayısı;
        /// </summary>
        public int SampleCount => _samples.Count;

        /// <summary>
        /// Aktif durum;
        /// </summary>
        public bool IsActive;
        {
            get => _isActive;
            set;
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnActiveStateChanged(value);
                }
            }
        }

        /// <summary>
        /// Blend eşik değeri;
        /// </summary>
        public float BlendThreshold;
        {
            get => _blendThreshold;
            set;
            {
                if (Math.Abs(_blendThreshold - value) > float.Epsilon)
                {
                    _blendThreshold = Math.Max(0.0f, Math.Min(1.0f, value));
                    OnBlendThresholdChanged(_blendThreshold);
                }
            }
        }

        /// <summary>
        /// Maksimum örnek sayısı;
        /// </summary>
        public int MaxSamples;
        {
            get => _maxSamples;
            set;
            {
                if (_maxSamples != value)
                {
                    _maxSamples = Math.Max(1, value);
                    OnMaxSamplesChanged(_maxSamples);
                }
            }
        }

        /// <summary>
        /// Geçerli blend sonucu;
        /// </summary>
        public AnimationSample CurrentResult => _currentResult;

        /// <summary>
        /// Geçerli parametre değerleri;
        /// </summary>
        public Vector3 CurrentParameterValues => _currentParameterValues;

        /// <summary>
        /// Geçerli ağırlıklar;
        /// </summary>
        public IReadOnlyList<WeightedSample> CurrentWeights => _currentWeights;

        /// <summary>
        /// Blend Space yapılandırması;
        /// </summary>
        public BlendSpaceConfig Config { get; private set; }

        /// <summary>
        /// BlendSpace sınıfı yapıcı metodu;
        /// </summary>
        public BlendSpace(
            ILogger logger,
            IErrorReporter errorReporter,
            IEventBus eventBus,
            IAnimationBlender animationBlender,
            IStateMachine stateMachine)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorReporter = errorReporter ?? throw new ArgumentNullException(nameof(errorReporter));
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _animationBlender = animationBlender ?? throw new ArgumentNullException(nameof(animationBlender));
            _stateMachine = stateMachine ?? throw new ArgumentNullException(nameof(stateMachine));

            Id = Guid.NewGuid().ToString();
            _dimensions = new Dictionary<string, BlendSpaceDimension>();
            _samples = new Dictionary<string, AnimationSample>();
            _parameters = new Dictionary<string, BlendSpaceParameter>();
            _grids = new Dictionary<string, BlendSpaceGrid>();

            _currentWeights = new List<WeightedSample>();
            Config = new BlendSpaceConfig();

            _logger.Info($"BlendSpace created with ID: {Id}");
        }

        /// <summary>
        /// Blend Space'i başlat;
        /// </summary>
        public async Task InitializeAsync(BlendSpaceConfig config = null)
        {
            try
            {
                _logger.Info($"Initializing BlendSpace '{Id}'...");

                if (config != null)
                {
                    Config = config;
                }

                // Alt bileşenleri başlat;
                await _animationBlender.InitializeAsync();
                await _stateMachine.InitializeAsync();

                // Varsayılan boyutları oluştur;
                await CreateDefaultDimensionsAsync();

                // Varsayından grid oluştur;
                await CreateDefaultGridAsync();

                // Olay dinleyicilerini kaydet;
                RegisterEventHandlers();

                _isInitialized = true;
                IsActive = true;

                _logger.Info($"BlendSpace '{Id}' initialized successfully");

                // Olay yayınla;
                BlendSpaceCreated?.Invoke(this, new BlendSpaceCreatedEventArgs;
                {
                    BlendSpaceId = Id,
                    BlendSpaceName = Name,
                    Type = Type,
                    Timestamp = DateTime.UtcNow;
                });

                _eventBus.Publish(new BlendSpaceInitializedEvent;
                {
                    BlendSpaceId = Id,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to initialize BlendSpace '{Id}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.InitializeAsync");
                throw new BlendSpaceInitializationException(
                    $"Failed to initialize BlendSpace '{Id}'", ex);
            }
        }

        /// <summary>
        /// Blend Space adını ayarla;
        /// </summary>
        public void SetName(string name)
        {
            ValidateInitialized();

            try
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Blend space name cannot be null or empty", nameof(name));
                }

                Name = name;
                _logger.Info($"BlendSpace name set to: {name}");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to set blend space name: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Boyut ekle;
        /// </summary>
        public async Task<BlendSpaceDimension> AddDimensionAsync(string dimensionName, BlendDimensionConfig config)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Adding dimension '{dimensionName}' to BlendSpace '{Id}'");

                if (_dimensions.ContainsKey(dimensionName))
                {
                    throw new DimensionAlreadyExistsException($"Dimension '{dimensionName}' already exists");
                }

                if (_dimensions.Count >= 3)
                {
                    throw new MaxDimensionsExceededException("Blend space supports maximum 3 dimensions");
                }

                var dimension = new BlendSpaceDimension(dimensionName, config);
                _dimensions[dimensionName] = dimension;

                // Parametre oluştur;
                var parameter = new BlendSpaceParameter;
                {
                    Name = dimensionName,
                    Value = config.DefaultValue,
                    MinValue = config.MinValue,
                    MaxValue = config.MaxValue,
                    IsNormalized = config.IsNormalized;
                };
                _parameters[dimensionName] = parameter;

                // Tipi güncelle;
                UpdateType();

                _logger.Info($"Dimension '{dimensionName}' added successfully");

                // Grid'i güncelle;
                await UpdateGridForNewDimensionAsync(dimension);

                return dimension;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add dimension '{dimensionName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.AddDimensionAsync");
                throw;
            }
        }

        /// <summary>
        /// Boyutu kaldır;
        /// </summary>
        public async Task RemoveDimensionAsync(string dimensionName)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Removing dimension '{dimensionName}' from BlendSpace '{Id}'");

                if (!_dimensions.ContainsKey(dimensionName))
                {
                    throw new DimensionNotFoundException($"Dimension '{dimensionName}' not found");
                }

                // İlgili örnekleri kontrol et;
                var samplesUsingDimension = _samples.Values;
                    .Where(s => s.ParameterValues.ContainsKey(dimensionName))
                    .ToList();

                if (samplesUsingDimension.Any())
                {
                    throw new DimensionInUseException(
                        $"Dimension '{dimensionName}' is used by {samplesUsingDimension.Count} samples");
                }

                _dimensions.Remove(dimensionName);
                _parameters.Remove(dimensionName);

                // Tipi güncelle;
                UpdateType();

                // Grid'i güncelle;
                await UpdateGridForRemovedDimensionAsync(dimensionName);

                _logger.Info($"Dimension '{dimensionName}' removed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to remove dimension '{dimensionName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.RemoveDimensionAsync");
                throw;
            }
        }

        /// <summary>
        /// Örnek ekle;
        /// </summary>
        public async Task<AnimationSample> AddSampleAsync(string sampleName, AnimationClip clip,
            Dictionary<string, float> parameterValues, SampleConfig config = null)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Adding sample '{sampleName}' to BlendSpace '{Id}'");

                if (_samples.ContainsKey(sampleName))
                {
                    throw new SampleAlreadyExistsException($"Sample '{sampleName}' already exists");
                }

                if (_samples.Count >= MaxSamples)
                {
                    throw new MaxSamplesExceededException(
                        $"Maximum samples limit reached ({MaxSamples})");
                }

                // Parametre değerlerini doğrula;
                ValidateParameterValues(parameterValues);

                var sample = new AnimationSample(sampleName, clip, parameterValues, config);
                _samples[sampleName] = sample;

                // Grid'e örneği ekle;
                if (ActiveGrid != null)
                {
                    await ActiveGrid.AddSampleAsync(sample);
                }

                _logger.Info($"Sample '{sampleName}' added successfully");

                // Olay yayınla;
                SampleAdded?.Invoke(this, new SampleAddedEventArgs;
                {
                    BlendSpaceId = Id,
                    SampleName = sampleName,
                    ParameterValues = parameterValues,
                    Timestamp = DateTime.UtcNow;
                });

                return sample;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to add sample '{sampleName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.AddSampleAsync");
                throw;
            }
        }

        /// <summary>
        /// Örnek kaldır;
        /// </summary>
        public async Task RemoveSampleAsync(string sampleName)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Removing sample '{sampleName}' from BlendSpace '{Id}'");

                if (!_samples.TryGetValue(sampleName, out var sample))
                {
                    throw new SampleNotFoundException($"Sample '{sampleName}' not found");
                }

                // Grid'den örneği kaldır;
                if (ActiveGrid != null)
                {
                    await ActiveGrid.RemoveSampleAsync(sampleName);
                }

                _samples.Remove(sampleName);

                _logger.Info($"Sample '{sampleName}' removed successfully");

                // Olay yayınla;
                SampleRemoved?.Invoke(this, new SampleRemovedEventArgs;
                {
                    BlendSpaceId = Id,
                    SampleName = sampleName,
                    Timestamp = DateTime.UtcNow;
                });
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to remove sample '{sampleName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.RemoveSampleAsync");
                throw;
            }
        }

        /// <summary>
        /// Grid oluştur;
        /// </summary>
        public async Task<BlendSpaceGrid> CreateGridAsync(string gridName, GridType gridType, GridConfig config)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Creating grid '{gridName}' for BlendSpace '{Id}'");

                if (_grids.ContainsKey(gridName))
                {
                    throw new GridAlreadyExistsException($"Grid '{gridName}' already exists");
                }

                var grid = new BlendSpaceGrid(gridName, gridType, config, _dimensions.Values.ToList());

                // Mevcut örnekleri grid'e ekle;
                foreach (var sample in _samples.Values)
                {
                    await grid.AddSampleAsync(sample);
                }

                _grids[gridName] = grid;

                // Aktif grid yap;
                _activeGridId = gridName;

                _logger.Info($"Grid '{gridName}' created successfully");

                // Olay yayınla;
                GridChanged?.Invoke(this, new GridChangedEventArgs;
                {
                    BlendSpaceId = Id,
                    GridName = gridName,
                    GridType = gridType,
                    Timestamp = DateTime.UtcNow;
                });

                return grid;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to create grid '{gridName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.CreateGridAsync");
                throw;
            }
        }

        /// <summary>
        /// Grid'i aktif yap;
        /// </summary>
        public async Task SetActiveGridAsync(string gridName)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Setting active grid to '{gridName}'");

                if (!_grids.ContainsKey(gridName))
                {
                    throw new GridNotFoundException($"Grid '{gridName}' not found");
                }

                if (_activeGridId != gridName)
                {
                    _activeGridId = gridName;

                    // Yeni grid'e göre blend hesapla;
                    await RecalculateBlendAsync();

                    _logger.Info($"Active grid set to '{gridName}'");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to set active grid: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.SetActiveGridAsync");
                throw;
            }
        }

        /// <summary>
        /// Parametre değerini güncelle;
        /// </summary>
        public async Task UpdateParameterAsync(string parameterName, float value)
        {
            ValidateInitialized();

            try
            {
                if (!_parameters.TryGetValue(parameterName, out var parameter))
                {
                    throw new ParameterNotFoundException($"Parameter '{parameterName}' not found");
                }

                // Değeri sınırla;
                var clampedValue = Math.Clamp(value, parameter.MinValue, parameter.MaxValue);

                if (Math.Abs(parameter.Value - clampedValue) > float.Epsilon)
                {
                    parameter.Value = clampedValue;

                    // Vector3'ü güncelle;
                    UpdateCurrentParameterValues();

                    // Blend hesapla;
                    await CalculateBlendAsync();

                    // Olay yayınla;
                    ParameterChanged?.Invoke(this, new ParameterChangedEventArgs;
                    {
                        BlendSpaceId = Id,
                        ParameterName = parameterName,
                        Value = clampedValue,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Debug($"Parameter '{parameterName}' updated to {clampedValue}");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update parameter '{parameterName}': {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.UpdateParameterAsync");
                throw;
            }
        }

        /// <summary>
        /// Tüm parametreleri güncelle;
        /// </summary>
        public async Task UpdateParametersAsync(Dictionary<string, float> parameterValues)
        {
            ValidateInitialized();

            try
            {
                bool anyChanged = false;

                foreach (var kvp in parameterValues)
                {
                    if (_parameters.TryGetValue(kvp.Key, out var parameter))
                    {
                        var clampedValue = Math.Clamp(kvp.Value, parameter.MinValue, parameter.MaxValue);

                        if (Math.Abs(parameter.Value - clampedValue) > float.Epsilon)
                        {
                            parameter.Value = clampedValue;
                            anyChanged = true;
                        }
                    }
                }

                if (anyChanged)
                {
                    UpdateCurrentParameterValues();
                    await CalculateBlendAsync();

                    _logger.Debug("Multiple parameters updated");
                }
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to update parameters: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.UpdateParametersAsync");
                throw;
            }
        }

        /// <summary>
        /// Blend hesapla;
        /// </summary>
        public async Task<AnimationSample> CalculateBlendAsync()
        {
            ValidateInitialized();

            try
            {
                if (ActiveGrid == null)
                {
                    throw new NoActiveGridException("No active grid available for blending");
                }

                if (_samples.Count == 0)
                {
                    throw new NoSamplesException("No samples available for blending");
                }

                // Grid'den blend ağırlıklarını al;
                var weights = await ActiveGrid.CalculateWeightsAsync(_currentParameterValues);
                _currentWeights = weights;

                // Animasyonları blend'le;
                AnimationSample result = null;

                if (weights.Count == 1)
                {
                    // Tek örnek varsa direkt kullan;
                    result = weights[0].Sample;
                }
                else if (weights.Count > 1)
                {
                    // Birden fazla örnek varsa blend yap;
                    result = await _animationBlender.BlendAnimationsAsync(weights);
                }

                if (result != null && (_currentResult == null ||
                    !_currentResult.Id.Equals(result.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    _currentResult = result;

                    // Olay yayınla;
                    BlendResultChanged?.Invoke(this, new BlendResultChangedEventArgs;
                    {
                        BlendSpaceId = Id,
                        ResultSample = result,
                        Weights = weights,
                        ParameterValues = _currentParameterValues,
                        Timestamp = DateTime.UtcNow;
                    });

                    _logger.Debug($"Blend calculated. Result: {result.Name}");
                }

                return _currentResult;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to calculate blend: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.CalculateBlendAsync");
                throw;
            }
        }

        /// <summary>
        /// En yakın örnekleri bul;
        /// </summary>
        public async Task<List<WeightedSample>> FindNearestSamplesAsync(Vector3 point, int maxResults = 4)
        {
            ValidateInitialized();

            try
            {
                if (ActiveGrid == null)
                {
                    throw new NoActiveGridException("No active grid available");
                }

                var samples = await ActiveGrid.FindNearestSamplesAsync(point, maxResults);
                return samples;
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to find nearest samples: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.FindNearestSamplesAsync");
                throw;
            }
        }

        /// <summary>
        /// Örnek ara;
        /// </summary>
        public List<AnimationSample> SearchSamples(string searchTerm)
        {
            return _samples.Values;
                .Where(s => s.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Blend Space'i optimize et;
        /// </summary>
        public async Task OptimizeAsync(OptimizationConfig config)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Optimizing BlendSpace '{Id}'");

                // Grid'i optimize et;
                if (ActiveGrid != null)
                {
                    await ActiveGrid.OptimizeAsync(config);
                }

                // Örnekleri optimize et;
                foreach (var sample in _samples.Values)
                {
                    if (sample.Config.EnableOptimization)
                    {
                        await sample.OptimizeAsync(config);
                    }
                }

                _logger.Info($"BlendSpace '{Id}' optimized successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to optimize blend space: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.OptimizeAsync");
                throw;
            }
        }

        /// <summary>
        /// Blend Space'i kaydet;
        /// </summary>
        public async Task SaveAsync(string filePath)
        {
            ValidateInitialized();

            try
            {
                _logger.Info($"Saving BlendSpace '{Id}' to {filePath}");

                var data = new BlendSpaceData;
                {
                    Id = Id,
                    Name = Name,
                    Type = Type,
                    Dimensions = _dimensions.Values.ToList(),
                    Samples = _samples.Values.ToList(),
                    Parameters = _parameters.Values.ToList(),
                    Config = Config,
                    ActiveGridId = _activeGridId;
                };

                // Serialize ve kaydet;
                await FileSystem.SaveJsonAsync(filePath, data);

                _logger.Info($"BlendSpace '{Id}' saved successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to save blend space: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.SaveAsync");
                throw;
            }
        }

        /// <summary>
        /// Blend Space'i yükle;
        /// </summary>
        public async Task LoadAsync(string filePath)
        {
            try
            {
                _logger.Info($"Loading BlendSpace from {filePath}");

                // Veriyi yükle;
                var data = await FileSystem.LoadJsonAsync<BlendSpaceData>(filePath);

                // Mevcut verileri temizle;
                _dimensions.Clear();
                _samples.Clear();
                _parameters.Clear();
                _grids.Clear();

                // Verileri yükle;
                Id = data.Id;
                Name = data.Name;
                Type = data.Type;

                foreach (var dimension in data.Dimensions)
                {
                    _dimensions[dimension.Name] = dimension;
                }

                foreach (var sample in data.Samples)
                {
                    _samples[sample.Name] = sample;
                }

                foreach (var parameter in data.Parameters)
                {
                    _parameters[parameter.Name] = parameter;
                }

                Config = data.Config ?? new BlendSpaceConfig();
                _activeGridId = data.ActiveGridId;

                // Grid'i yeniden oluştur;
                await RecreateGridsFromDataAsync(data);

                // Parametre değerlerini güncelle;
                UpdateCurrentParameterValues();

                _isInitialized = true;

                _logger.Info($"BlendSpace '{Id}' loaded successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Failed to load blend space: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.LoadAsync");
                throw;
            }
        }

        /// <summary>
        /// Tüm boyutları al;
        /// </summary>
        public IReadOnlyList<BlendSpaceDimension> GetDimensions()
        {
            return _dimensions.Values.ToList();
        }

        /// <summary>
        /// Tüm örnekleri al;
        /// </summary>
        public IReadOnlyList<AnimationSample> GetSamples()
        {
            return _samples.Values.ToList();
        }

        /// <summary>
        /// Tüm parametreleri al;
        /// </summary>
        public IReadOnlyList<BlendSpaceParameter> GetParameters()
        {
            return _parameters.Values.ToList();
        }

        /// <summary>
        /// Tüm grid'leri al;
        /// </summary>
        public IReadOnlyList<BlendSpaceGrid> GetGrids()
        {
            return _grids.Values.ToList();
        }

        /// <summary>
        /// Kaynakları serbest bırak;
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                _logger.Info($"Disposing BlendSpace '{Id}'...");

                IsActive = false;

                // Grid'leri dispose et;
                foreach (var grid in _grids.Values)
                {
                    if (grid is IAsyncDisposable disposableGrid)
                    {
                        await disposableGrid.DisposeAsync();
                    }
                }

                // Örnekleri dispose et;
                foreach (var sample in _samples.Values)
                {
                    if (sample is IAsyncDisposable disposableSample)
                    {
                        await disposableSample.DisposeAsync();
                    }
                }

                // Kaynakları temizle;
                _dimensions.Clear();
                _samples.Clear();
                _parameters.Clear();
                _grids.Clear();
                _currentWeights.Clear();

                _currentResult = null;
                _isInitialized = false;

                _logger.Info($"BlendSpace '{Id}' disposed successfully");
            }
            catch (Exception ex)
            {
                _logger.Error($"Error disposing BlendSpace: {ex.Message}", ex);
                await _errorReporter.ReportErrorAsync(ex, "BlendSpace.DisposeAsync");
            }
        }

        #region Private Methods;

        private async Task CreateDefaultDimensionsAsync()
        {
            try
            {
                // Varsayılan boyutlar;
                if (Config.CreateDefaultDimensions)
                {
                    await AddDimensionAsync("Speed", new BlendDimensionConfig;
                    {
                        MinValue = 0.0f,
                        MaxValue = 10.0f,
                        DefaultValue = 0.0f,
                        IsNormalized = false,
                        DisplayName = "Speed",
                        Units = "m/s"
                    });

                    await AddDimensionAsync("Direction", new BlendDimensionConfig;
                    {
                        MinValue = -180.0f,
                        MaxValue = 180.0f,
                        DefaultValue = 0.0f,
                        IsNormalized = true,
                        DisplayName = "Direction",
                        Units = "degrees"
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to create default dimensions: {ex.Message}");
            }
        }

        private async Task CreateDefaultGridAsync()
        {
            try
            {
                if (_dimensions.Count > 0)
                {
                    var gridType = _dimensions.Count switch;
                    {
                        1 => GridType.Linear,
                        2 => GridType.Rectangular,
                        3 => GridType.Volumetric,
                        _ => GridType.Custom;
                    };

                    await CreateGridAsync("DefaultGrid", gridType, new GridConfig;
                    {
                        CellSize = 1.0f,
                        EnableInterpolation = true,
                        InterpolationMethod = InterpolationMethod.Bilinear,
                        EnableExtrapolation = Config.EnableExtrapolation,
                        MaxExtrapolationDistance = Config.MaxExtrapolationDistance;
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"Failed to create default grid: {ex.Message}");
            }
        }

        private async Task UpdateGridForNewDimensionAsync(BlendSpaceDimension newDimension)
        {
            foreach (var grid in _grids.Values)
            {
                await grid.AddDimensionAsync(newDimension);
            }
        }

        private async Task UpdateGridForRemovedDimensionAsync(string dimensionName)
        {
            foreach (var grid in _grids.Values)
            {
                await grid.RemoveDimensionAsync(dimensionName);
            }
        }

        private async Task RecreateGridsFromDataAsync(BlendSpaceData data)
        {
            // Grid'leri yeniden oluştur;
            // Not: Bu implementasyon grid'in serialize edilmiş haline bağlı;
            // Gerçek implementasyonda grid verilerini de kaydedip yüklemek gerekir;
            await CreateDefaultGridAsync();
        }

        private async Task RecalculateBlendAsync()
        {
            if (IsActive && _samples.Count > 0)
            {
                await CalculateBlendAsync();
            }
        }

        private void UpdateCurrentParameterValues()
        {
            var values = Vector3.Zero;

            // İlk 3 parametreyi al;
            var paramList = _parameters.Values.ToList();
            for (int i = 0; i < Math.Min(3, paramList.Count); i++)
            {
                switch (i)
                {
                    case 0: values.X = paramList[i].Value; break;
                    case 1: values.Y = paramList[i].Value; break;
                    case 2: values.Z = paramList[i].Value; break;
                }
            }

            _currentParameterValues = values;
        }

        private void UpdateType()
        {
            Type = _dimensions.Count switch;
            {
                1 => BlendSpaceType.OneDimensional,
                2 => BlendSpaceType.TwoDimensional,
                3 => BlendSpaceType.ThreeDimensional,
                _ => BlendSpaceType.Custom;
            };
        }

        private void ValidateParameterValues(Dictionary<string, float> parameterValues)
        {
            foreach (var kvp in parameterValues)
            {
                if (!_dimensions.ContainsKey(kvp.Key))
                {
                    throw new DimensionNotFoundException(
                        $"Dimension '{kvp.Key}' not found in blend space");
                }

                var dimension = _dimensions[kvp.Key];
                if (kvp.Value < dimension.Config.MinValue || kvp.Value > dimension.Config.MaxValue)
                {
                    throw new ParameterValueOutOfRangeException(
                        $"Value {kvp.Value} for dimension '{kvp.Key}' is out of range " +
                        $"[{dimension.Config.MinValue}, {dimension.Config.MaxValue}]");
                }
            }
        }

        private void RegisterEventHandlers()
        {
            // Grid olaylarını dinle;
            if (ActiveGrid != null)
            {
                ActiveGrid.GridModified += OnGridModified;
                ActiveGrid.SamplesModified += OnGridSamplesModified;
            }

            // State machine olaylarını dinle;
            _stateMachine.StateChanged += OnStateMachineStateChanged;
        }

        private void ValidateInitialized()
        {
            if (!_isInitialized)
            {
                throw new BlendSpaceNotInitializedException(
                    "BlendSpace must be initialized before use");
            }
        }

        #endregion;

        #region Event Handlers;

        private void OnGridModified(object sender, GridModifiedEventArgs e)
        {
            // Grid değişikliklerini işle;
            _logger.Debug($"Grid modified: {e.GridName}");
        }

        private void OnGridSamplesModified(object sender, SamplesModifiedEventArgs e)
        {
            // Örnek değişikliklerini işle;
            _logger.Debug($"Grid samples modified: {e.ModifiedSamples.Count} samples");
        }

        private void OnStateMachineStateChanged(object sender, StateChangedEventArgs e)
        {
            // State machine durum değişikliklerini işle;
            if (e.NewState == AnimationState.Blending && IsActive)
            {
                _ = RecalculateBlendAsync();
            }
        }

        private void OnTypeChanged(BlendSpaceType newType)
        {
            _eventBus.Publish(new BlendSpaceTypeChangedEvent;
            {
                BlendSpaceId = Id,
                NewType = newType,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnActiveStateChanged(bool isActive)
        {
            _eventBus.Publish(new BlendSpaceActiveStateChangedEvent;
            {
                BlendSpaceId = Id,
                IsActive = isActive,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnBlendThresholdChanged(float newThreshold)
        {
            _eventBus.Publish(new BlendThresholdChangedEvent;
            {
                BlendSpaceId = Id,
                Threshold = newThreshold,
                Timestamp = DateTime.UtcNow;
            });
        }

        private void OnMaxSamplesChanged(int newMax)
        {
            _eventBus.Publish(new MaxSamplesChangedEvent;
            {
                BlendSpaceId = Id,
                MaxSamples = newMax,
                Timestamp = DateTime.UtcNow;
            });
        }

        #endregion;

        #region IDisposable Implementation;

        public void Dispose()
        {
            DisposeAsync().AsTask().Wait();
            GC.SuppressFinalize(this);
        }

        #endregion;
    }

    #region Data Classes;

    /// <summary>
    /// Blend Space boyutu;
    /// </summary>
    public class BlendSpaceDimension;
    {
        public string Name { get; }
        public BlendDimensionConfig Config { get; }
        public DateTime Created { get; }

        public BlendSpaceDimension(string name, BlendDimensionConfig config)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Created = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Blend Space parametresi;
    /// </summary>
    public class BlendSpaceParameter;
    {
        public string Name { get; set; }
        public float Value { get; set; }
        public float MinValue { get; set; }
        public float MaxValue { get; set; }
        public bool IsNormalized { get; set; }
        public string DisplayName { get; set; }
        public string Units { get; set; }
    }

    /// <summary>
    /// Animasyon örneği;
    /// </summary>
    public class AnimationSample : IAsyncDisposable;
    {
        public string Id { get; }
        public string Name { get; }
        public AnimationClip Clip { get; }
        public Dictionary<string, float> ParameterValues { get; }
        public SampleConfig Config { get; }
        public DateTime Created { get; }
        public DateTime LastUsed { get; private set; }
        public int UsageCount { get; private set; }

        public AnimationSample(string name, AnimationClip clip,
            Dictionary<string, float> parameterValues, SampleConfig config = null)
        {
            Id = Guid.NewGuid().ToString();
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Clip = clip ?? throw new ArgumentNullException(nameof(clip));
            ParameterValues = parameterValues ?? throw new ArgumentNullException(nameof(parameterValues));
            Config = config ?? new SampleConfig();
            Created = DateTime.UtcNow;
            LastUsed = DateTime.UtcNow;
            UsageCount = 0;
        }

        public void MarkUsed()
        {
            LastUsed = DateTime.UtcNow;
            UsageCount++;
        }

        public async Task OptimizeAsync(OptimizationConfig config)
        {
            // Animasyon optimizasyonu yap;
            await Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            // Animasyon kaynaklarını serbest bırak;
            if (Clip is IAsyncDisposable disposableClip)
            {
                await disposableClip.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Ağırlıklı örnek;
    /// </summary>
    public class WeightedSample;
    {
        public AnimationSample Sample { get; }
        public float Weight { get; }
        public float Distance { get; }

        public WeightedSample(AnimationSample sample, float weight, float distance)
        {
            Sample = sample ?? throw new ArgumentNullException(nameof(sample));
            Weight = Math.Clamp(weight, 0.0f, 1.0f);
            Distance = Math.Max(0.0f, distance);
        }
    }

    /// <summary>
    /// Blend Space verisi;
    /// </summary>
    public class BlendSpaceData;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public BlendSpaceType Type { get; set; }
        public List<BlendSpaceDimension> Dimensions { get; set; }
        public List<AnimationSample> Samples { get; set; }
        public List<BlendSpaceParameter> Parameters { get; set; }
        public BlendSpaceConfig Config { get; set; }
        public string ActiveGridId { get; set; }
        public DateTime SavedAt { get; set; }

        public BlendSpaceData()
        {
            Dimensions = new List<BlendSpaceDimension>();
            Samples = new List<AnimationSample>();
            Parameters = new List<BlendSpaceParameter>();
            SavedAt = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Blend Space konfigürasyonu;
    /// </summary>
    public class BlendSpaceConfig;
    {
        public bool CreateDefaultDimensions { get; set; } = true;
        public bool EnableExtrapolation { get; set; } = true;
        public float MaxExtrapolationDistance { get; set; } = 2.0f;
        public bool AutoCalculateBlend { get; set; } = true;
        public float AutoCalculateInterval { get; set; } = 0.1f; // saniye;
        public bool EnableCaching { get; set; } = true;
        public int CacheSize { get; set; } = 1000;
        public bool EnableLogging { get; set; } = true;
        public LogLevel LogLevel { get; set; } = LogLevel.Info;
    }

    /// <summary>
    /// Boyut konfigürasyonu;
    /// </summary>
    public class BlendDimensionConfig;
    {
        public float MinValue { get; set; }
        public float MaxValue { get; set; }
        public float DefaultValue { get; set; }
        public bool IsNormalized { get; set; }
        public string DisplayName { get; set; }
        public string Units { get; set; }
        public int GridDivisions { get; set; } = 10;
        public bool WrapAround { get; set; } = false;
    }

    /// <summary>
    /// Örnek konfigürasyonu;
    /// </summary>
    public class SampleConfig;
    {
        public float InfluenceRadius { get; set; } = 1.0f;
        public bool EnableOptimization { get; set; } = true;
        public OptimizationLevel OptimizationLevel { get; set; } = OptimizationLevel.Medium;
        public bool CacheResult { get; set; } = true;
        public int Priority { get; set; } = 1;
    }

    /// <summary>
    /// Grid konfigürasyonu;
    /// </summary>
    public class GridConfig;
    {
        public float CellSize { get; set; } = 1.0f;
        public bool EnableInterpolation { get; set; } = true;
        public InterpolationMethod InterpolationMethod { get; set; } = InterpolationMethod.Bilinear;
        public bool EnableExtrapolation { get; set; } = true;
        public float MaxExtrapolationDistance { get; set; } = 2.0f;
        public bool EnableCaching { get; set; } = true;
        public int MaxCacheSize { get; set; } = 1000;
        public bool PrecomputeWeights { get; set; } = true;
    }

    /// <summary>
    /// Optimizasyon konfigürasyonu;
    /// </summary>
    public class OptimizationConfig;
    {
        public bool OptimizeMemory { get; set; } = true;
        public bool OptimizePerformance { get; set; } = true;
        public OptimizationLevel Level { get; set; } = OptimizationLevel.Medium;
        public float QualityThreshold { get; set; } = 0.9f;
        public int MaxIterations { get; set; } = 100;
    }

    #endregion;

    #region Enums;

    /// <summary>
    /// Blend Space tipi;
    /// </summary>
    public enum BlendSpaceType;
    {
        OneDimensional,
        TwoDimensional,
        ThreeDimensional,
        Custom;
    }

    /// <summary>
    /// Grid tipi;
    /// </summary>
    public enum GridType;
    {
        Linear,
        Rectangular,
        Triangular,
        Volumetric,
        Hexagonal,
        Custom;
    }

    /// <summary>
    /// Interpolasyon metodu;
    /// </summary>
    public enum InterpolationMethod;
    {
        Linear,
        Bilinear,
        Trilinear,
        Barycentric,
        InverseDistance,
        RadialBasisFunction;
    }

    /// <summary>
    /// Optimizasyon seviyesi;
    /// </summary>
    public enum OptimizationLevel;
    {
        None,
        Low,
        Medium,
        High,
        Maximum;
    }

    /// <summary>
    /// Animasyon durumu;
    /// </summary>
    public enum AnimationState;
    {
        Idle,
        Playing,
        Paused,
        Blending,
        Stopped;
    }

    /// <summary>
    /// Log seviyesi;
    /// </summary>
    public enum LogLevel;
    {
        Debug,
        Info,
        Warning,
        Error,
        Critical;
    }

    #endregion;

    #region Event Classes;

    public class BlendSpaceCreatedEventArgs : EventArgs;
    {
        public string BlendSpaceId { get; set; }
        public string BlendSpaceName { get; set; }
        public BlendSpaceType Type { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SampleAddedEventArgs : EventArgs;
    {
        public string BlendSpaceId { get; set; }
        public string SampleName { get; set; }
        public Dictionary<string, float> ParameterValues { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SampleRemovedEventArgs : EventArgs;
    {
        public string BlendSpaceId { get; set; }
        public string SampleName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ParameterChangedEventArgs : EventArgs;
    {
        public string BlendSpaceId { get; set; }
        public string ParameterName { get; set; }
        public float Value { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendResultChangedEventArgs : EventArgs;
    {
        public string BlendSpaceId { get; set; }
        public AnimationSample ResultSample { get; set; }
        public List<WeightedSample> Weights { get; set; }
        public Vector3 ParameterValues { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class GridChangedEventArgs : EventArgs;
    {
        public string BlendSpaceId { get; set; }
        public string GridName { get; set; }
        public GridType GridType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class GridModifiedEventArgs : EventArgs;
    {
        public string GridName { get; set; }
        public GridModificationType ModificationType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class SamplesModifiedEventArgs : EventArgs;
    {
        public List<string> ModifiedSamples { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StateChangedEventArgs : EventArgs;
    {
        public AnimationState OldState { get; set; }
        public AnimationState NewState { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum GridModificationType;
    {
        DimensionsChanged,
        SamplesAdded,
        SamplesRemoved,
        ConfigUpdated,
        Optimized;
    }

    #endregion;

    #region Event Bus Events;

    public class BlendSpaceInitializedEvent : IEvent;
    {
        public string BlendSpaceId { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendSpaceTypeChangedEvent : IEvent;
    {
        public string BlendSpaceId { get; set; }
        public BlendSpaceType NewType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendSpaceActiveStateChangedEvent : IEvent;
    {
        public string BlendSpaceId { get; set; }
        public bool IsActive { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class BlendThresholdChangedEvent : IEvent;
    {
        public string BlendSpaceId { get; set; }
        public float Threshold { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MaxSamplesChangedEvent : IEvent;
    {
        public string BlendSpaceId { get; set; }
        public int MaxSamples { get; set; }
        public DateTime Timestamp { get; set; }
    }

    #endregion;

    #region Exceptions;

    public class BlendSpaceException : Exception
    {
        public BlendSpaceException(string message) : base(message) { }
        public BlendSpaceException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class BlendSpaceInitializationException : BlendSpaceException;
    {
        public BlendSpaceInitializationException(string message) : base(message) { }
        public BlendSpaceInitializationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class BlendSpaceNotInitializedException : BlendSpaceException;
    {
        public BlendSpaceNotInitializedException(string message) : base(message) { }
    }

    public class DimensionAlreadyExistsException : BlendSpaceException;
    {
        public DimensionAlreadyExistsException(string message) : base(message) { }
    }

    public class DimensionNotFoundException : BlendSpaceException;
    {
        public DimensionNotFoundException(string message) : base(message) { }
    }

    public class DimensionInUseException : BlendSpaceException;
    {
        public DimensionInUseException(string message) : base(message) { }
    }

    public class MaxDimensionsExceededException : BlendSpaceException;
    {
        public MaxDimensionsExceededException(string message) : base(message) { }
    }

    public class SampleAlreadyExistsException : BlendSpaceException;
    {
        public SampleAlreadyExistsException(string message) : base(message) { }
    }

    public class SampleNotFoundException : BlendSpaceException;
    {
        public SampleNotFoundException(string message) : base(message) { }
    }

    public class MaxSamplesExceededException : BlendSpaceException;
    {
        public MaxSamplesExceededException(string message) : base(message) { }
    }

    public class ParameterNotFoundException : BlendSpaceException;
    {
        public ParameterNotFoundException(string message) : base(message) { }
    }

    public class ParameterValueOutOfRangeException : BlendSpaceException;
    {
        public ParameterValueOutOfRangeException(string message) : base(message) { }
    }

    public class GridAlreadyExistsException : BlendSpaceException;
    {
        public GridAlreadyExistsException(string message) : base(message) { }
    }

    public class GridNotFoundException : BlendSpaceException;
    {
        public GridNotFoundException(string message) : base(message) { }
    }

    public class NoActiveGridException : BlendSpaceException;
    {
        public NoActiveGridException(string message) : base(message) { }
    }

    public class NoSamplesException : BlendSpaceException;
    {
        public NoSamplesException(string message) : base(message) { }
    }

    #endregion;

    #region Interfaces;

    public interface IBlendSpace : IAsyncDisposable;
    {
        event EventHandler<BlendSpaceCreatedEventArgs> BlendSpaceCreated;
        event EventHandler<SampleAddedEventArgs> SampleAdded;
        event EventHandler<SampleRemovedEventArgs> SampleRemoved;
        event EventHandler<ParameterChangedEventArgs> ParameterChanged;
        event EventHandler<BlendResultChangedEventArgs> BlendResultChanged;
        event EventHandler<GridChangedEventArgs> GridChanged;

        string Id { get; }
        string Name { get; }
        BlendSpaceType Type { get; }
        int DimensionCount { get; }
        BlendSpaceGrid ActiveGrid { get; }
        int SampleCount { get; }
        bool IsActive { get; set; }
        float BlendThreshold { get; set; }
        int MaxSamples { get; set; }
        AnimationSample CurrentResult { get; }
        Vector3 CurrentParameterValues { get; }
        IReadOnlyList<WeightedSample> CurrentWeights { get; }
        BlendSpaceConfig Config { get; }

        Task InitializeAsync(BlendSpaceConfig config = null);
        void SetName(string name);
        Task<BlendSpaceDimension> AddDimensionAsync(string dimensionName, BlendDimensionConfig config);
        Task RemoveDimensionAsync(string dimensionName);
        Task<AnimationSample> AddSampleAsync(string sampleName, AnimationClip clip,
            Dictionary<string, float> parameterValues, SampleConfig config = null);
        Task RemoveSampleAsync(string sampleName);
        Task<BlendSpaceGrid> CreateGridAsync(string gridName, GridType gridType, GridConfig config);
        Task SetActiveGridAsync(string gridName);
        Task UpdateParameterAsync(string parameterName, float value);
        Task UpdateParametersAsync(Dictionary<string, float> parameterValues);
        Task<AnimationSample> CalculateBlendAsync();
        Task<List<WeightedSample>> FindNearestSamplesAsync(Vector3 point, int maxResults = 4);
        List<AnimationSample> SearchSamples(string searchTerm);
        Task OptimizeAsync(OptimizationConfig config);
        Task SaveAsync(string filePath);
        Task LoadAsync(string filePath);
        IReadOnlyList<BlendSpaceDimension> GetDimensions();
        IReadOnlyList<AnimationSample> GetSamples();
        IReadOnlyList<BlendSpaceParameter> GetParameters();
        IReadOnlyList<BlendSpaceGrid> GetGrids();
    }

    // Referans için diğer arayüzler;
    public interface IAnimationBlender { /* ... */ }
    public interface IStateMachine { /* ... */ }
    public interface IBlendSpaceGrid { /* ... */ }
    public class BlendSpaceGrid : IBlendSpaceGrid { /* ... */ }
    public class AnimationClip { /* ... */ }

    #endregion;

    #region Utility Classes;

    public static class FileSystem;
    {
        public static async Task SaveJsonAsync<T>(string filePath, T data)
        {
            // JSON serileştirme implementasyonu;
            await Task.CompletedTask;
        }

        public static async Task<T> LoadJsonAsync<T>(string filePath)
        {
            // JSON deserileştirme implementasyonu;
            await Task.CompletedTask;
            return default;
        }
    }

    #endregion;
}
