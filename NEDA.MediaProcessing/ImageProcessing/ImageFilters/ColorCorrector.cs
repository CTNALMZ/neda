using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using NEDA.Logging;
using NEDA.ExceptionHandling;
using NEDA.MediaProcessing.ImageProcessing.Interfaces;
using NEDA.Common.Utilities;
using NEDA.Common.Extensions;

namespace NEDA.MediaProcessing.ImageProcessing.ImageFilters;
{
    /// <summary>
    /// Gelişmiş renk düzeltme ve ayarlama işlemleri sağlayan sınıf;
    /// </summary>
    public class ColorCorrector : IColorCorrector, IDisposable;
    {
        #region Private Fields;

        private readonly ILogger _logger;
        private readonly IErrorHandler _errorHandler;
        private readonly IColorSpaceConverter _colorSpaceConverter;
        private readonly IHistogramAnalyzer _histogramAnalyzer;

        private readonly object _processingLock = new object();
        private bool _isProcessing;
        private CorrectionProfile _currentProfile;
        private readonly Dictionary<string, CorrectionProfile> _savedProfiles;
        private readonly ColorCorrectionCache _correctionCache;

        // Optimizasyon için önceden hesaplanmış değerler;
        private float[] _gammaLookupTable;
        private float[] _contrastLookupTable;
        private float[] _brightnessLookupTable;

        private const int GAMMA_TABLE_SIZE = 4096;
        private const int CONTRAST_TABLE_SIZE = 256;
        private const int BRIGHTNESS_TABLE_SIZE = 256;
        private const float DEFAULT_GAMMA = 2.2f;
        private const float MIN_GAMMA = 0.1f;
        private const float MAX_GAMMA = 10.0f;

        #endregion;

        #region Properties;

        /// <summary>
        /// Aktif düzeltme profili;
        /// </summary>
        public CorrectionProfile ActiveProfile => _currentProfile;

        /// <summary>
        /// Kayıtlı profil sayısı;
        /// </summary>
        public int ProfileCount => _savedProfiles.Count;

        /// <summary>
        /// Otomatik düzeltme özelliği aktif mi?
        /// </summary>
        public bool AutoCorrectionEnabled { get; set; }

        /// <summary>
        /// Gerçek zamanlı önizleme özelliği aktif mi?
        /// </summary>
        public bool RealtimePreviewEnabled { get; set; }

        /// <summary>
        /// Histogram eşitleme özelliği aktif mi?
        /// </summary>
        public bool HistogramEqualizationEnabled { get; set; }

        /// <summary>
        /// Renk kanallarını bağımsız düzenleme izni;
        /// </summary>
        public bool IndependentChannelAdjustment { get; set; }

        /// <summary>
        /// Yüksek doğruluk modu (daha yavaş, daha hassas)
        /// </summary>
        public bool HighPrecisionMode { get; set; }

        /// <summary>
        /// Paralel işleme kullanılsın mı?
        /// </summary>
        public bool UseParallelProcessing { get; set; } = true;

        /// <summary>
        /// Maksimum paralel iş parçacığı sayısı;
        /// </summary>
        public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

        /// <summary>
        /// Önbellek boyutu (MB)
        /// </summary>
        public int CacheSizeMB { get; set; } = 100;

        #endregion;

        #region Constructor;

        /// <summary>
        /// ColorCorrector sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        public ColorCorrector(
            ILogger logger,
            IErrorHandler errorHandler,
            IColorSpaceConverter colorSpaceConverter = null,
            IHistogramAnalyzer histogramAnalyzer = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _errorHandler = errorHandler ?? throw new ArgumentNullException(nameof(errorHandler));
            _colorSpaceConverter = colorSpaceConverter;
            _histogramAnalyzer = histogramAnalyzer;

            _savedProfiles = new Dictionary<string, CorrectionProfile>();
            _correctionCache = new ColorCorrectionCache(CacheSizeMB);
            _currentProfile = CorrectionProfile.CreateDefault();

            InitializeLookupTables();

            _logger.Info("ColorCorrector initialized", GetType().Name);
        }

        #endregion;

        #region Public Methods - Basic Adjustments;

        /// <summary>
        /// Görüntünün parlaklığını ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="brightness">Parlaklık değeri (-1.0 to 1.0)</param>
        /// <param name="preserveHighlights">Parlak bölgeleri koru</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustBrightnessAsync(
            ImageData image,
            float brightness,
            bool preserveHighlights = false,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateBrightness(brightness);

            var profile = _currentProfile.Clone();
            profile.Brightness = MathHelper.Clamp(brightness, -1.0f, 1.0f);

            return await ApplyColorCorrectionAsync(image, profile, preserveHighlights, cancellationToken);
        }

        /// <summary>
        /// Görüntünün kontrastını ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="contrast">Kontrast değeri (-1.0 to 1.0)</param>
        /// <param name="adaptive">Uyarlamalı kontrast kullan</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustContrastAsync(
            ImageData image,
            float contrast,
            bool adaptive = false,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateContrast(contrast);

            if (adaptive)
            {
                return await ApplyAdaptiveContrastAsync(image, contrast, cancellationToken);
            }

            var profile = _currentProfile.Clone();
            profile.Contrast = MathHelper.Clamp(contrast, -1.0f, 1.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün doygunluğunu ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="saturation">Doygunluk değeri (-1.0 to 1.0)</param>
        /// <param name="preserveSkinTones">Cilt tonlarını koru</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustSaturationAsync(
            ImageData image,
            float saturation,
            bool preserveSkinTones = false,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateSaturation(saturation);

            var profile = _currentProfile.Clone();
            profile.Saturation = MathHelper.Clamp(saturation, -1.0f, 1.0f);
            profile.PreserveSkinTones = preserveSkinTones;

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün renk tonunu ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="hue">Renk tonu değeri (-180 to 180)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustHueAsync(
            ImageData image,
            float hue,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateHue(hue);

            var profile = _currentProfile.Clone();
            profile.Hue = MathHelper.Clamp(hue, -180.0f, 180.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün gama değerini ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="gamma">Gama değeri (0.1 to 10.0)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustGammaAsync(
            ImageData image,
            float gamma,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateGamma(gamma);

            var profile = _currentProfile.Clone();
            profile.Gamma = MathHelper.Clamp(gamma, MIN_GAMMA, MAX_GAMMA);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün sıcaklığını ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="temperature">Sıcaklık değeri (-100 to 100)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustTemperatureAsync(
            ImageData image,
            float temperature,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateTemperature(temperature);

            var profile = _currentProfile.Clone();
            profile.Temperature = MathHelper.Clamp(temperature, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün tint (yeşil/magenta) dengesini ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="tint">Tint değeri (-100 to 100)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustTintAsync(
            ImageData image,
            float tint,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateTint(tint);

            var profile = _currentProfile.Clone();
            profile.Tint = MathHelper.Clamp(tint, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün exposure (pozlamasını) ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="exposure">Pozlama değeri (-5.0 to 5.0)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustExposureAsync(
            ImageData image,
            float exposure,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateExposure(exposure);

            var profile = _currentProfile.Clone();
            profile.Exposure = MathHelper.Clamp(exposure, -5.0f, 5.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün highlights (parlak bölgelerini) ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="highlights">Highlight değeri (-100 to 100)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustHighlightsAsync(
            ImageData image,
            float highlights,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateHighlights(highlights);

            var profile = _currentProfile.Clone();
            profile.Highlights = MathHelper.Clamp(highlights, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün shadows (gölge bölgelerini) ayarlar;
        /// </summary>
        /// <param name="image">Görüntü verisi</param>
        /// <param name="shadows">Shadow değeri (-100 to 100)</param>
        /// <param name="cancellationToken">İptal token'ı</param>
        public async Task<ImageData> AdjustShadowsAsync(
            ImageData image,
            float shadows,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateShadows(shadows);

            var profile = _currentProfile.Clone();
            profile.Shadows = MathHelper.Clamp(shadows, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün whites (beyaz noktalarını) ayarlar;
        /// </summary>
        public async Task<ImageData> AdjustWhitesAsync(
            ImageData image,
            float whites,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateWhites(whites);

            var profile = _currentProfile.Clone();
            profile.Whites = MathHelper.Clamp(whites, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün blacks (siyah noktalarını) ayarlar;
        /// </summary>
        public async Task<ImageData> AdjustBlacksAsync(
            ImageData image,
            float blacks,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateBlacks(blacks);

            var profile = _currentProfile.Clone();
            profile.Blacks = MathHelper.Clamp(blacks, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün vibrance (canlılığını) ayarlar;
        /// </summary>
        public async Task<ImageData> AdjustVibranceAsync(
            ImageData image,
            float vibrance,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateVibrance(vibrance);

            var profile = _currentProfile.Clone();
            profile.Vibrance = MathHelper.Clamp(vibrance, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün clarity (netliğini) ayarlar;
        /// </summary>
        public async Task<ImageData> AdjustClarityAsync(
            ImageData image,
            float clarity,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateClarity(clarity);

            var profile = _currentProfile.Clone();
            profile.Clarity = MathHelper.Clamp(clarity, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Görüntünün dehaze (sis giderme) ayarını yapar;
        /// </summary>
        public async Task<ImageData> AdjustDehazeAsync(
            ImageData image,
            float dehaze,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateDehaze(dehaze);

            var profile = _currentProfile.Clone();
            profile.Dehaze = MathHelper.Clamp(dehaze, -100.0f, 100.0f);

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        #endregion;

        #region Public Methods - Advanced Corrections;

        /// <summary>
        /// Birden fazla renk düzeltmesini tek seferde uygular;
        /// </summary>
        public async Task<ImageData> ApplyColorCorrectionAsync(
            ImageData image,
            CorrectionProfile profile,
            bool preserveHighlights = false,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateProfile(profile);

            lock (_processingLock)
            {
                if (_isProcessing)
                    throw new InvalidOperationException("Another color correction operation is in progress");
                _isProcessing = true;
            }

            try
            {
                _logger.Info($"Applying color correction profile: {profile.Name}", GetType().Name);

                // Önbellek kontrolü;
                var cacheKey = GenerateCacheKey(image, profile);
                if (_correctionCache.TryGet(cacheKey, out var cachedImage))
                {
                    _logger.Debug("Retrieved from cache", GetType().Name);
                    return cachedImage;
                }

                // Görüntüyü kopyala;
                var result = CloneImageData(image);

                // Renk uzayı dönüşümü gerekli mi?
                var requiresColorSpaceConversion = profile.WorkingColorSpace != ColorSpace.RGB;
                if (requiresColorSpaceConversion && _colorSpaceConverter != null)
                {
                    result = await _colorSpaceConverter.ConvertToAsync(
                        result, profile.WorkingColorSpace, cancellationToken);
                }

                // Düzeltmeleri uygula;
                await ApplyCorrectionsToImageAsync(result, profile, preserveHighlights, cancellationToken);

                // Orijinal renk uzayına geri dön;
                if (requiresColorSpaceConversion && _colorSpaceConverter != null)
                {
                    result = await _colorSpaceConverter.ConvertFromAsync(
                        result, profile.WorkingColorSpace, cancellationToken);
                }

                // Önbelleğe ekle;
                _correctionCache.Add(cacheKey, result);

                // Aktif profili güncelle;
                _currentProfile = profile;

                return result;
            }
            catch (Exception ex)
            {
                _logger.Error($"Color correction failed: {ex.Message}", GetType().Name);
                _errorHandler.HandleError(ex, ErrorCodes.COLOR_CORRECTION_FAILED);
                throw;
            }
            finally
            {
                lock (_processingLock)
                {
                    _isProcessing = false;
                }
            }
        }

        /// <summary>
        /// Otomatik renk düzeltmesi uygular;
        /// </summary>
        public async Task<ImageData> AutoCorrectAsync(
            ImageData image,
            AutoCorrectionOptions options = null,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);

            options ??= new AutoCorrectionOptions();

            var profile = await AnalyzeAndCreateCorrectionProfileAsync(image, options, cancellationToken);
            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Renk kanallarını bağımsız olarak ayarlar;
        /// </summary>
        public async Task<ImageData> AdjustChannelsIndependentlyAsync(
            ImageData image,
            ChannelAdjustments redChannel,
            ChannelAdjustments greenChannel,
            ChannelAdjustments blueChannel,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);

            if (!IndependentChannelAdjustment)
                throw new InvalidOperationException("Independent channel adjustment is disabled");

            var profile = _currentProfile.Clone();
            profile.RedChannel = redChannel;
            profile.GreenChannel = greenChannel;
            profile.BlueChannel = blueChannel;
            profile.IndependentChannelAdjustment = true;

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Renk eğrilerini (curves) uygular;
        /// </summary>
        public async Task<ImageData> ApplyCurvesAsync(
            ImageData image,
            ToneCurve rgbCurve,
            ToneCurve redCurve = null,
            ToneCurve greenCurve = null,
            ToneCurve blueCurve = null,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateToneCurve(rgbCurve);

            var profile = _currentProfile.Clone();
            profile.RGBCurve = rgbCurve;
            profile.RedCurve = redCurve;
            profile.GreenCurve = greenCurve;
            profile.BlueCurve = blueCurve;

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Renk seçicisi ile belirli bir rengi ayarlar;
        /// </summary>
        public async Task<ImageData> AdjustSpecificColorAsync(
            ImageData image,
            ColorRange targetColor,
            ColorAdjustment adjustment,
            float range = 30.0f,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateColorRange(targetColor);
            ValidateColorAdjustment(adjustment);

            var profile = _currentProfile.Clone();
            profile.ColorTargeting = new ColorTargeting;
            {
                TargetColor = targetColor,
                Adjustment = adjustment,
                Range = MathHelper.Clamp(range, 0.0f, 180.0f)
            };

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Split toning (bölünmüş tonlama) uygular;
        /// </summary>
        public async Task<ImageData> ApplySplitToningAsync(
            ImageData image,
            Toning highlightsToning,
            Toning shadowsToning,
            float balance = 0.0f,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateToning(highlightsToning);
            ValidateToning(shadowsToning);

            var profile = _currentProfile.Clone();
            profile.SplitToning = new SplitToning;
            {
                Highlights = highlightsToning,
                Shadows = shadowsToning,
                Balance = MathHelper.Clamp(balance, -100.0f, 100.0f)
            };

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        /// <summary>
        /// Renk derecelendirme (color grading) uygular;
        /// </summary>
        public async Task<ImageData> ApplyColorGradingAsync(
            ImageData image,
            LookupTable lookupTable,
            float intensity = 1.0f,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);
            ValidateLookupTable(lookupTable);

            var profile = _currentProfile.Clone();
            profile.ColorGrading = new ColorGrading;
            {
                LookupTable = lookupTable,
                Intensity = MathHelper.Clamp(intensity, 0.0f, 2.0f)
            };

            return await ApplyColorCorrectionAsync(image, profile, false, cancellationToken);
        }

        #endregion;

        #region Public Methods - Profile Management;

        /// <summary>
        /// Düzeltme profilini kaydeder;
        /// </summary>
        public void SaveProfile(string name, CorrectionProfile profile)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Profile name cannot be null or empty", nameof(name));

            ValidateProfile(profile);

            var normalizedName = name.Trim();
            _savedProfiles[normalizedName] = profile.Clone();

            _logger.Info($"Profile saved: {normalizedName}", GetType().Name);
        }

        /// <summary>
        /// Düzeltme profilini yükler;
        /// </summary>
        public CorrectionProfile LoadProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Profile name cannot be null or empty", nameof(name));

            var normalizedName = name.Trim();

            if (!_savedProfiles.TryGetValue(normalizedName, out var profile))
                throw new KeyNotFoundException($"Profile not found: {normalizedName}");

            _currentProfile = profile.Clone();

            _logger.Info($"Profile loaded: {normalizedName}", GetType().Name);
            return _currentProfile;
        }

        /// <summary>
        /// Düzeltme profilini siler;
        /// </summary>
        public bool DeleteProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Profile name cannot be null or empty", nameof(name));

            var normalizedName = name.Trim();
            var removed = _savedProfiles.Remove(normalizedName);

            if (removed)
            {
                _logger.Info($"Profile deleted: {normalizedName}", GetType().Name);
            }

            return removed;
        }

        /// <summary>
        /// Kayıtlı tüm profilleri listeler;
        /// </summary>
        public IEnumerable<string> ListProfiles()
        {
            return _savedProfiles.Keys.OrderBy(k => k);
        }

        /// <summary>
        /// Profili dışa aktarır;
        /// </summary>
        public string ExportProfile(CorrectionProfile profile)
        {
            ValidateProfile(profile);
            return profile.Serialize();
        }

        /// <summary>
        /// Profili içe aktarır;
        /// </summary>
        public CorrectionProfile ImportProfile(string profileData, string name = null)
        {
            if (string.IsNullOrWhiteSpace(profileData))
                throw new ArgumentException("Profile data cannot be null or empty", nameof(profileData));

            var profile = CorrectionProfile.Deserialize(profileData);

            if (!string.IsNullOrWhiteSpace(name))
            {
                profile.Name = name.Trim();
                SaveProfile(profile.Name, profile);
            }

            _currentProfile = profile;

            _logger.Info($"Profile imported: {profile.Name}", GetType().Name);
            return profile;
        }

        /// <summary>
        /// Varsayılan profili yükler;
        /// </summary>
        public void ResetToDefault()
        {
            _currentProfile = CorrectionProfile.CreateDefault();
            _logger.Info("Reset to default profile", GetType().Name);
        }

        #endregion;

        #region Public Methods - Analysis;

        /// <summary>
        /// Görüntünün histogramını analiz eder;
        /// </summary>
        public async Task<HistogramAnalysis> AnalyzeHistogramAsync(
            ImageData image,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);

            if (_histogramAnalyzer == null)
                throw new InvalidOperationException("Histogram analyzer is not available");

            return await _histogramAnalyzer.AnalyzeAsync(image, cancellationToken);
        }

        /// <summary>
        /// Görüntünün renk dengesini analiz eder;
        /// </summary>
        public async Task<ColorBalanceAnalysis> AnalyzeColorBalanceAsync(
            ImageData image,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);

            var histogram = await AnalyzeHistogramAsync(image, cancellationToken);
            return CalculateColorBalance(histogram);
        }

        /// <summary>
        /// Görüntünün renk sıcaklığını tahmin eder;
        /// </summary>
        public async Task<float> EstimateColorTemperatureAsync(
            ImageData image,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);

            var balance = await AnalyzeColorBalanceAsync(image, cancellationToken);

            // Renk sıcaklığı hesaplama;
            var r = balance.RedPercentage;
            var g = balance.GreenPercentage;
            var b = balance.BluePercentage;

            if (r + g + b == 0)
                return 6500; // Nötr sıcaklık;

            var temp = 6500 + (r - b) * 1000;
            return MathHelper.Clamp(temp, 1000, 12000);
        }

        /// <summary>
        /// Görüntü için önerilen düzeltmeleri hesaplar;
        /// </summary>
        public async Task<CorrectionSuggestions> GetCorrectionSuggestionsAsync(
            ImageData image,
            CancellationToken cancellationToken = default)
        {
            ValidateImage(image);

            var histogram = await AnalyzeHistogramAsync(image, cancellationToken);
            var balance = await AnalyzeColorBalanceAsync(image, cancellationToken);
            var temperature = await EstimateColorTemperatureAsync(image, cancellationToken);

            return CalculateCorrectionSuggestions(histogram, balance, temperature);
        }

        #endregion;

        #region Private Methods - Core Processing;

        /// <summary>
        /// Düzeltmeleri görüntüye uygular;
        /// </summary>
        private async Task ApplyCorrectionsToImageAsync(
            ImageData image,
            CorrectionProfile profile,
            bool preserveHighlights,
            CancellationToken cancellationToken)
        {
            var width = image.Width;
            var height = image.Height;
            var stride = image.Stride;
            var bytesPerPixel = stride / width;

            var pixelCount = width * height;
            var totalBytes = pixelCount * bytesPerPixel;

            // Paralel işleme stratejisi;
            var partitionCount = UseParallelProcessing ? MaxDegreeOfParallelism : 1;
            var partitionSize = totalBytes / partitionCount;

            var tasks = new List<Task>();

            for (int i = 0; i < partitionCount; i++)
            {
                var startIndex = i * partitionSize;
                var endIndex = (i == partitionCount - 1) ? totalBytes : startIndex + partitionSize;

                tasks.Add(Task.Run(() =>
                {
                    ProcessImagePartition(
                        image.Data,
                        startIndex,
                        endIndex,
                        bytesPerPixel,
                        profile,
                        preserveHighlights,
                        cancellationToken);
                }, cancellationToken));
            }

            await Task.WhenAll(tasks);

            // Özel efektleri uygula;
            await ApplySpecialEffectsAsync(image, profile, cancellationToken);
        }

        /// <summary>
        /// Görüntü bölümünü işler;
        /// </summary>
        private void ProcessImagePartition(
            byte[] imageData,
            int startIndex,
            int endIndex,
            int bytesPerPixel,
            CorrectionProfile profile,
            bool preserveHighlights,
            CancellationToken cancellationToken)
        {
            for (int i = startIndex; i < endIndex; i += bytesPerPixel)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // RGBA formatı varsayıyoruz;
                var b = imageData[i];
                var g = imageData[i + 1];
                var r = imageData[i + 2];
                var a = bytesPerPixel > 3 ? imageData[i + 3] : (byte)255;

                // Parlak bölge koruma kontrolü;
                if (preserveHighlights && IsHighlight(r, g, b))
                    continue;

                // Renk düzeltmelerini uygula;
                ApplyColorCorrections(ref r, ref g, ref b, profile);

                // Gama düzeltmesi;
                if (profile.Gamma != DEFAULT_GAMMA)
                {
                    r = ApplyGammaCorrection(r, profile.Gamma);
                    g = ApplyGammaCorrection(g, profile.Gamma);
                    b = ApplyGammaCorrection(b, profile.Gamma);
                }

                // Kontrast;
                if (Math.Abs(profile.Contrast) > 0.001f)
                {
                    r = ApplyContrast(r, profile.Contrast);
                    g = ApplyContrast(g, profile.Contrast);
                    b = ApplyContrast(b, profile.Contrast);
                }

                // Parlaklık;
                if (Math.Abs(profile.Brightness) > 0.001f)
                {
                    r = ApplyBrightness(r, profile.Brightness);
                    g = ApplyBrightness(g, profile.Brightness);
                    b = ApplyBrightness(b, profile.Brightness);
                }

                // Doygunluk;
                if (Math.Abs(profile.Saturation) > 0.001f)
                {
                    ApplySaturation(ref r, ref g, ref b, profile.Saturation, profile.PreserveSkinTones);
                }

                // Renk tonu;
                if (Math.Abs(profile.Hue) > 0.001f)
                {
                    ApplyHueShift(ref r, ref g, ref b, profile.Hue);
                }

                // Bağımsız kanal ayarları;
                if (profile.IndependentChannelAdjustment)
                {
                    ApplyChannelAdjustments(ref r, ref g, ref b, profile);
                }

                // Sıcaklık ve Tint;
                if (Math.Abs(profile.Temperature) > 0.001f || Math.Abs(profile.Tint) > 0.001f)
                {
                    ApplyTemperatureAndTint(ref r, ref g, ref b, profile.Temperature, profile.Tint);
                }

                // Pozlama;
                if (Math.Abs(profile.Exposure) > 0.001f)
                {
                    ApplyExposure(ref r, ref g, ref b, profile.Exposure);
                }

                // Renk eğrileri;
                if (profile.RGBCurve != null)
                {
                    r = profile.RGBCurve.Apply(r);
                    g = profile.RGBCurve.Apply(g);
                    b = profile.RGBCurve.Apply(b);
                }

                // Sınırları kontrol et;
                r = ClampByte(r);
                g = ClampByte(g);
                b = ClampByte(b);

                // Geri yaz;
                imageData[i] = b;
                imageData[i + 1] = g;
                imageData[i + 2] = r;
                if (bytesPerPixel > 3)
                    imageData[i + 3] = a;
            }
        }

        /// <summary>
        /// Temel renk düzeltmelerini uygular;
        /// </summary>
        private void ApplyColorCorrections(ref byte r, ref byte g, ref byte b, CorrectionProfile profile)
        {
            // Vibrance;
            if (Math.Abs(profile.Vibrance) > 0.001f)
            {
                ApplyVibrance(ref r, ref g, ref b, profile.Vibrance);
            }

            // Clarity (yerel kontrast)
            if (Math.Abs(profile.Clarity) > 0.001f)
            {
                // Bu implementasyon için basit bir kontrast artışı;
                // Gerçek implementasyon daha karmaşık olmalı;
                var clarity = profile.Clarity / 100.0f;
                r = ApplyLocalContrast(r, clarity);
                g = ApplyLocalContrast(g, clarity);
                b = ApplyLocalContrast(b, clarity);
            }

            // Dehaze;
            if (Math.Abs(profile.Dehaze) > 0.001f)
            {
                ApplyDehaze(ref r, ref g, ref b, profile.Dehaze);
            }
        }

        /// <summary>
        /// Özel efektleri uygular;
        /// </summary>
        private async Task ApplySpecialEffectsAsync(
            ImageData image,
            CorrectionProfile profile,
            CancellationToken cancellationToken)
        {
            // Split toning;
            if (profile.SplitToning != null)
            {
                await ApplySplitToningEffectAsync(image, profile.SplitToning, cancellationToken);
            }

            // Color grading;
            if (profile.ColorGrading != null && profile.ColorGrading.Intensity > 0.001f)
            {
                ApplyColorGradingEffect(image, profile.ColorGrading);
            }

            // Color targeting;
            if (profile.ColorTargeting != null)
            {
                await ApplyColorTargetingEffectAsync(image, profile.ColorTargeting, cancellationToken);
            }
        }

        #endregion;

        #region Private Methods - Color Correction Algorithms;

        /// <summary>
        /// Gama düzeltmesi uygular;
        /// </summary>
        private byte ApplyGammaCorrection(byte value, float gamma)
        {
            var normalized = value / 255.0f;
            var corrected = (float)Math.Pow(normalized, 1.0f / gamma);
            return (byte)(corrected * 255);
        }

        /// <summary>
        /// Kontrast uygular;
        /// </summary>
        private byte ApplyContrast(byte value, float contrast)
        {
            var factor = (259.0f * (contrast + 255.0f)) / (255.0f * (259.0f - contrast));
            var result = factor * (value - 128.0f) + 128.0f;
            return ClampByte(result);
        }

        /// <summary>
        /// Parlaklık uygular;
        /// </summary>
        private byte ApplyBrightness(byte value, float brightness)
        {
            var result = value + brightness * 255.0f;
            return ClampByte(result);
        }

        /// <summary>
        /// Doygunluk uygular;
        /// </summary>
        private void ApplySaturation(ref byte r, ref byte g, ref byte b, float saturation, bool preserveSkinTones)
        {
            // RGB'yi HSL'ye dönüştür;
            var (h, s, l) = RgbToHsl(r, g, b);

            // Cilt tonu koruması;
            if (preserveSkinTones && IsSkinTone(h, s, l))
            {
                // Cilt tonları için daha az doygunluk değişikliği;
                s *= 1.0f + (saturation * 0.5f);
            }
            else;
            {
                s *= 1.0f + saturation;
            }

            s = MathHelper.Clamp(s, 0.0f, 1.0f);

            // HSL'yi RGB'ye geri dönüştür;
            (r, g, b) = HslToRgb(h, s, l);
        }

        /// <summary>
        /// Renk tonu kaydırması uygular;
        /// </summary>
        private void ApplyHueShift(ref byte r, ref byte g, ref byte b, float hueShift)
        {
            var (h, s, l) = RgbToHsl(r, g, b);

            h = (h + hueShift / 360.0f) % 1.0f;
            if (h < 0) h += 1.0f;

            (r, g, b) = HslToRgb(h, s, l);
        }

        /// <summary>
        /// Vibrance uygular;
        /// </summary>
        private void ApplyVibrance(ref byte r, ref byte g, ref byte b, float vibrance)
        {
            var (h, s, l) = RgbToHsl(r, g, b);

            // Düşük doygunluklu renkleri daha fazla artır;
            var boost = 1.0f + (vibrance / 100.0f) * (1.0f - s);
            s *= boost;
            s = MathHelper.Clamp(s, 0.0f, 1.0f);

            (r, g, b) = HslToRgb(h, s, l);
        }

        /// <summary>
        /// Sıcaklık ve Tint uygular;
        /// </summary>
        private void ApplyTemperatureAndTint(ref byte r, ref byte g, ref byte b, float temperature, float tint)
        {
            // Sıcaklık (mavi/sarı)
            if (Math.Abs(temperature) > 0.001f)
            {
                var tempAdjust = temperature / 100.0f;
                if (temperature > 0)
                {
                    // Daha sıcak (daha sarı)
                    r = ClampByte(r + tempAdjust * 50);
                    b = ClampByte(b - tempAdjust * 50);
                }
                else;
                {
                    // Daha soğuk (daha mavi)
                    r = ClampByte(r + tempAdjust * 50);
                    b = ClampByte(b - tempAdjust * 50);
                }
            }

            // Tint (yeşil/magenta)
            if (Math.Abs(tint) > 0.001f)
            {
                var tintAdjust = tint / 100.0f;
                if (tint > 0)
                {
                    // Daha yeşil;
                    g = ClampByte(g + tintAdjust * 30);
                }
                else;
                {
                    // Daha magenta;
                    r = ClampByte(r - tintAdjust * 30);
                    b = ClampByte(b - tintAdjust * 30);
                }
            }
        }

        /// <summary>
        /// Pozlama uygular;
        /// </summary>
        private void ApplyExposure(ref byte r, ref byte g, ref byte b, float exposure)
        {
            var factor = (float)Math.Pow(2.0, exposure);

            r = ClampByte(r * factor);
            g = ClampByte(g * factor);
            b = ClampByte(b * factor);
        }

        /// <summary>
        /// Sis giderme uygular;
        /// </summary>
        private void ApplyDehaze(ref byte r, ref byte g, ref byte b, float dehaze)
        {
            // Basit bir dehaze algoritması;
            var avg = (r + g + b) / 3.0f / 255.0f;
            var amount = dehaze / 100.0f;

            var contrastBoost = 1.0f + amount;
            var brightnessAdjust = -amount * avg * 0.5f;

            r = ApplyContrast(r, amount);
            g = ApplyContrast(g, amount);
            b = ApplyContrast(b, amount);

            r = ClampByte(r + brightnessAdjust * 255);
            g = ClampByte(g + brightnessAdjust * 255);
            b = ClampByte(b + brightnessAdjust * 255);
        }

        /// <summary>
        /// Yerel kontrast uygular;
        /// </summary>
        private byte ApplyLocalContrast(byte value, float clarity)
        {
            // Bu basit bir implementasyon;
            // Gerçek implementasyon için unsharp mask veya benzeri algoritmalar kullanılmalı;
            var result = value + clarity * (value - 128);
            return ClampByte(result);
        }

        /// <summary>
        /// Kanal ayarlarını uygular;
        /// </summary>
        private void ApplyChannelAdjustments(ref byte r, ref byte g, ref byte b, CorrectionProfile profile)
        {
            if (profile.RedChannel != null)
            {
                r = ApplyChannelAdjustment(r, profile.RedChannel);
            }

            if (profile.GreenChannel != null)
            {
                g = ApplyChannelAdjustment(g, profile.GreenChannel);
            }

            if (profile.BlueChannel != null)
            {
                b = ApplyChannelAdjustment(b, profile.BlueChannel);
            }
        }

        /// <summary>
        /// Tek kanal ayarını uygular;
        /// </summary>
        private byte ApplyChannelAdjustment(byte value, ChannelAdjustments adjustment)
        {
            var result = (float)value;

            result += adjustment.Brightness * 255.0f;
            result *= 1.0f + adjustment.Contrast;

            if (adjustment.Gamma != 1.0f)
            {
                var normalized = result / 255.0f;
                result = (float)Math.Pow(normalized, 1.0f / adjustment.Gamma) * 255.0f;
            }

            return ClampByte(result);
        }

        #endregion;

        #region Private Methods - Special Effects;

        /// <summary>
        /// Split toning efektini uygular;
        /// </summary>
        private async Task ApplySplitToningEffectAsync(
            ImageData image,
            SplitToning splitToning,
            CancellationToken cancellationToken)
        {
            // Implementasyon için placeholder;
            // Gerçek implementasyon görüntüyü highlights ve shadows olarak ayırıp;
            // her birine farklı toning uygulamalı;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Color grading efektini uygular;
        /// </summary>
        private void ApplyColorGradingEffect(ImageData image, ColorGrading colorGrading)
        {
            // LUT tabanlı color grading;
            // Implementasyon için placeholder;
        }

        /// <summary>
        /// Renk hedefleme efektini uygular;
        /// </summary>
        private async Task ApplyColorTargetingEffectAsync(
            ImageData image,
            ColorTargeting targeting,
            CancellationToken cancellationToken)
        {
            // Belirli bir renk aralığını hedefleyen ayarlama;
            // Implementasyon için placeholder;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Uyarlamalı kontrast uygular;
        /// </summary>
        private async Task<ImageData> ApplyAdaptiveContrastAsync(
            ImageData image,
            float contrast,
            CancellationToken cancellationToken)
        {
            // Histogram tabanlı uyarlamalı kontrast;
            // Implementasyon için placeholder;
            return image;
        }

        #endregion;

        #region Private Methods - Analysis and Suggestions;

        /// <summary>
        /// Görüntü analizi ile düzeltme profili oluşturur;
        /// </summary>
        private async Task<CorrectionProfile> AnalyzeAndCreateCorrectionProfileAsync(
            ImageData image,
            AutoCorrectionOptions options,
            CancellationToken cancellationToken)
        {
            var suggestions = await GetCorrectionSuggestionsAsync(image, cancellationToken);

            var profile = CorrectionProfile.CreateDefault();
            profile.Name = "Auto Corrected";

            // Önerilen düzeltmeleri uygula;
            profile.Brightness = suggestions.BrightnessCorrection;
            profile.Contrast = suggestions.ContrastCorrection;
            profile.Saturation = suggestions.SaturationCorrection;
            profile.Temperature = suggestions.TemperatureCorrection;

            if (options.AdjustExposure)
                profile.Exposure = suggestions.ExposureCorrection;

            if (options.AdjustHighlightsShadows)
            {
                profile.Highlights = suggestions.HighlightsCorrection;
                profile.Shadows = suggestions.ShadowsCorrection;
            }

            if (options.UseVibrance)
                profile.Vibrance = suggestions.VibranceCorrection;

            return profile;
        }

        /// <summary>
        /// Renk dengesini hesaplar;
        /// </summary>
        private ColorBalanceAnalysis CalculateColorBalance(HistogramAnalysis histogram)
        {
            // Basit bir renk dengesi hesaplaması;
            var totalRed = histogram.RedChannel.Sum();
            var totalGreen = histogram.GreenChannel.Sum();
            var totalBlue = histogram.BlueChannel.Sum();
            var total = totalRed + totalGreen + totalBlue;

            return new ColorBalanceAnalysis;
            {
                RedPercentage = total > 0 ? totalRed / total : 0.33f,
                GreenPercentage = total > 0 ? totalGreen / total : 0.33f,
                BluePercentage = total > 0 ? totalBlue / total : 0.33f,
                IsBalanced = Math.Abs(totalRed - totalGreen) < total * 0.1f &&
                            Math.Abs(totalGreen - totalBlue) < total * 0.1f;
            };
        }

        /// <summary>
        /// Düzeltme önerilerini hesaplar;
        /// </summary>
        private CorrectionSuggestions CalculateCorrectionSuggestions(
            HistogramAnalysis histogram,
            ColorBalanceAnalysis balance,
            float temperature)
        {
            var suggestions = new CorrectionSuggestions();

            // Parlaklık önerisi (histograma göre)
            var meanBrightness = histogram.LuminanceMean;
            suggestions.BrightnessCorrection = (0.5f - meanBrightness) * 0.5f;

            // Kontrast önerisi (standart sapmaya göre)
            var stdDev = histogram.LuminanceStdDev;
            suggestions.ContrastCorrection = (0.3f - stdDev) * 0.5f;

            // Doygunluk önerisi;
            var avgSaturation = histogram.SaturationMean;
            suggestions.SaturationCorrection = (0.5f - avgSaturation) * 0.3f;

            // Sıcaklık önerisi;
            suggestions.TemperatureCorrection = (6500 - temperature) / 100.0f;

            // Pozlama önerisi;
            var maxLuminance = histogram.LuminanceMax;
            suggestions.ExposureCorrection = (0.9f - maxLuminance) * 0.5f;

            return suggestions;
        }

        #endregion;

        #region Private Methods - Utilities;

        /// <summary>
        /// Lookup tablolarını başlatır;
        /// </summary>
        private void InitializeLookupTables()
        {
            // Gamma lookup table;
            _gammaLookupTable = new float[GAMMA_TABLE_SIZE];
            for (int i = 0; i < GAMMA_TABLE_SIZE; i++)
            {
                var value = i / (float)(GAMMA_TABLE_SIZE - 1);
                _gammaLookupTable[i] = value;
            }

            // Kontrast lookup table;
            _contrastLookupTable = new float[CONTRAST_TABLE_SIZE];
            for (int i = 0; i < CONTRAST_TABLE_SIZE; i++)
            {
                _contrastLookupTable[i] = i / 255.0f;
            }

            // Parlaklık lookup table;
            _brightnessLookupTable = new float[BRIGHTNESS_TABLE_SIZE];
            for (int i = 0; i < BRIGHTNESS_TABLE_SIZE; i++)
            {
                _brightnessLookupTable[i] = i / 255.0f;
            }
        }

        /// <summary>
        /// Görüntü verisini klonlar;
        /// </summary>
        private ImageData CloneImageData(ImageData source)
        {
            var clonedData = new byte[source.Data.Length];
            Array.Copy(source.Data, clonedData, source.Data.Length);

            return new ImageData;
            {
                Data = clonedData,
                Width = source.Width,
                Height = source.Height,
                Stride = source.Stride,
                Format = source.Format;
            };
        }

        /// <summary>
        /// Önbellek anahtarı oluşturur;
        /// </summary>
        private string GenerateCacheKey(ImageData image, CorrectionProfile profile)
        {
            var hash = $"{image.GetHashCode()}_{profile.GetHashCode()}";
            return hash;
        }

        /// <summary>
        /// RGB'yi HSL'ye dönüştürür;
        /// </summary>
        private (float H, float S, float L) RgbToHsl(byte r, byte g, byte b)
        {
            var rf = r / 255.0f;
            var gf = g / 255.0f;
            var bf = b / 255.0f;

            var max = Math.Max(rf, Math.Max(gf, bf));
            var min = Math.Min(rf, Math.Min(gf, bf));
            var delta = max - min;

            float h = 0, s = 0, l = (max + min) / 2.0f;

            if (delta > 0.001f)
            {
                s = delta / (1 - Math.Abs(2 * l - 1));

                if (Math.Abs(max - rf) < 0.001f)
                    h = (gf - bf) / delta + (gf < bf ? 6 : 0);
                else if (Math.Abs(max - gf) < 0.001f)
                    h = (bf - rf) / delta + 2;
                else;
                    h = (rf - gf) / delta + 4;

                h /= 6.0f;
            }

            return (h, s, l);
        }

        /// <summary>
        /// HSL'yi RGB'ye dönüştürür;
        /// </summary>
        private (byte R, byte G, byte B) HslToRgb(float h, float s, float l)
        {
            float r, g, b;

            if (s < 0.001f)
            {
                r = g = b = l;
            }
            else;
            {
                var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
                var p = 2 * l - q;

                r = HueToRgb(p, q, h + 1.0f / 3);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0f / 3);
            }

            return (
                (byte)(r * 255),
                (byte)(g * 255),
                (byte)(b * 255)
            );
        }

        /// <summary>
        /// Hue değerini RGB bileşenine dönüştürür;
        /// </summary>
        private float HueToRgb(float p, float q, float t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;

            if (t < 1.0f / 6) return p + (q - p) * 6 * t;
            if (t < 1.0f / 2) return q;
            if (t < 2.0f / 3) return p + (q - p) * (2.0f / 3 - t) * 6;
            return p;
        }

        /// <summary>
        /// Parlak bölge kontrolü;
        /// </summary>
        private bool IsHighlight(byte r, byte g, byte b)
        {
            var luminance = 0.299f * r + 0.587f * g + 0.114f * b;
            return luminance > 200;
        }

        /// <summary>
        /// Cilt tonu kontrolü;
        /// </summary>
        private bool IsSkinTone(float h, float s, float l)
        {
            // Basit cilt tonu tespiti;
            return h > 0.05f && h < 0.15f && s > 0.2f && s < 0.6f && l > 0.3f && l < 0.8f;
        }

        /// <summary>
        /// Byte değeri sınırlar;
        /// </summary>
        private byte ClampByte(float value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        private byte ClampByte(double value) => ClampByte((float)value);

        #endregion;

        #region Validation Methods;

        private void ValidateImage(ImageData image)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            if (image.Data == null || image.Data.Length == 0)
                throw new ArgumentException("Image data cannot be null or empty", nameof(image));

            if (image.Width <= 0 || image.Height <= 0)
                throw new ArgumentException("Image dimensions must be positive", nameof(image));

            if (image.Stride < image.Width * 3)
                throw new ArgumentException("Invalid image stride", nameof(image));
        }

        private void ValidateBrightness(float brightness)
        {
            if (brightness < -1.0f || brightness > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(brightness), "Brightness must be between -1.0 and 1.0");
        }

        private void ValidateContrast(float contrast)
        {
            if (contrast < -1.0f || contrast > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(contrast), "Contrast must be between -1.0 and 1.0");
        }

        private void ValidateSaturation(float saturation)
        {
            if (saturation < -1.0f || saturation > 1.0f)
                throw new ArgumentOutOfRangeException(nameof(saturation), "Saturation must be between -1.0 and 1.0");
        }

        private void ValidateHue(float hue)
        {
            if (hue < -180.0f || hue > 180.0f)
                throw new ArgumentOutOfRangeException(nameof(hue), "Hue must be between -180 and 180");
        }

        private void ValidateGamma(float gamma)
        {
            if (gamma < MIN_GAMMA || gamma > MAX_GAMMA)
                throw new ArgumentOutOfRangeException(nameof(gamma), $"Gamma must be between {MIN_GAMMA} and {MAX_GAMMA}");
        }

        private void ValidateTemperature(float temperature)
        {
            if (temperature < -100.0f || temperature > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(temperature), "Temperature must be between -100 and 100");
        }

        private void ValidateTint(float tint)
        {
            if (tint < -100.0f || tint > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(tint), "Tint must be between -100 and 100");
        }

        private void ValidateExposure(float exposure)
        {
            if (exposure < -5.0f || exposure > 5.0f)
                throw new ArgumentOutOfRangeException(nameof(exposure), "Exposure must be between -5.0 and 5.0");
        }

        private void ValidateHighlights(float highlights)
        {
            if (highlights < -100.0f || highlights > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(highlights), "Highlights must be between -100 and 100");
        }

        private void ValidateShadows(float shadows)
        {
            if (shadows < -100.0f || shadows > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(shadows), "Shadows must be between -100 and 100");
        }

        private void ValidateWhites(float whites)
        {
            if (whites < -100.0f || whites > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(whites), "Whites must be between -100 and 100");
        }

        private void ValidateBlacks(float blacks)
        {
            if (blacks < -100.0f || blacks > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(blacks), "Blacks must be between -100 and 100");
        }

        private void ValidateVibrance(float vibrance)
        {
            if (vibrance < -100.0f || vibrance > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(vibrance), "Vibrance must be between -100 and 100");
        }

        private void ValidateClarity(float clarity)
        {
            if (clarity < -100.0f || clarity > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(clarity), "Clarity must be between -100 and 100");
        }

        private void ValidateDehaze(float dehaze)
        {
            if (dehaze < -100.0f || dehaze > 100.0f)
                throw new ArgumentOutOfRangeException(nameof(dehaze), "Dehaze must be between -100 and 100");
        }

        private void ValidateProfile(CorrectionProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
        }

        private void ValidateToneCurve(ToneCurve curve)
        {
            if (curve == null)
                throw new ArgumentNullException(nameof(curve));
        }

        private void ValidateColorRange(ColorRange range)
        {
            if (range == null)
                throw new ArgumentNullException(nameof(range));
        }

        private void ValidateColorAdjustment(ColorAdjustment adjustment)
        {
            if (adjustment == null)
                throw new ArgumentNullException(nameof(adjustment));
        }

        private void ValidateToning(Toning toning)
        {
            if (toning == null)
                throw new ArgumentNullException(nameof(toning));
        }

        private void ValidateLookupTable(LookupTable lut)
        {
            if (lut == null)
                throw new ArgumentNullException(nameof(lut));
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
                    _correctionCache?.Clear();
                    _savedProfiles.Clear();
                    _logger.Info("ColorCorrector disposed", GetType().Name);
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~ColorCorrector()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    public enum ColorSpace;
    {
        RGB,
        HSL,
        HSV,
        LAB,
        XYZ,
        YUV,
        CMYK;
    }

    public class CorrectionProfile;
    {
        public string Name { get; set; } = "Default";
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Saturation { get; set; }
        public float Hue { get; set; }
        public float Gamma { get; set; } = 2.2f;
        public float Temperature { get; set; }
        public float Tint { get; set; }
        public float Exposure { get; set; }
        public float Highlights { get; set; }
        public float Shadows { get; set; }
        public float Whites { get; set; }
        public float Blacks { get; set; }
        public float Vibrance { get; set; }
        public float Clarity { get; set; }
        public float Dehaze { get; set; }
        public bool PreserveSkinTones { get; set; }
        public bool IndependentChannelAdjustment { get; set; }
        public ChannelAdjustments RedChannel { get; set; }
        public ChannelAdjustments GreenChannel { get; set; }
        public ChannelAdjustments BlueChannel { get; set; }
        public ToneCurve RGBCurve { get; set; }
        public ToneCurve RedCurve { get; set; }
        public ToneCurve GreenCurve { get; set; }
        public ToneCurve BlueCurve { get; set; }
        public ColorSpace WorkingColorSpace { get; set; } = ColorSpace.RGB;
        public ColorTargeting ColorTargeting { get; set; }
        public SplitToning SplitToning { get; set; }
        public ColorGrading ColorGrading { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime ModifiedDate { get; set; } = DateTime.Now;

        public static CorrectionProfile CreateDefault() => new CorrectionProfile;
        {
            Name = "Default",
            Gamma = 2.2f;
        };

        public CorrectionProfile Clone()
        {
            return new CorrectionProfile;
            {
                Name = this.Name,
                Brightness = this.Brightness,
                Contrast = this.Contrast,
                Saturation = this.Saturation,
                Hue = this.Hue,
                Gamma = this.Gamma,
                Temperature = this.Temperature,
                Tint = this.Tint,
                Exposure = this.Exposure,
                Highlights = this.Highlights,
                Shadows = this.Shadows,
                Whites = this.Whites,
                Blacks = this.Blacks,
                Vibrance = this.Vibrance,
                Clarity = this.Clarity,
                Dehaze = this.Dehaze,
                PreserveSkinTones = this.PreserveSkinTones,
                IndependentChannelAdjustment = this.IndependentChannelAdjustment,
                RedChannel = this.RedChannel?.Clone(),
                GreenChannel = this.GreenChannel?.Clone(),
                BlueChannel = this.BlueChannel?.Clone(),
                RGBCurve = this.RGBCurve?.Clone(),
                RedCurve = this.RedCurve?.Clone(),
                GreenCurve = this.GreenCurve?.Clone(),
                BlueCurve = this.BlueCurve?.Clone(),
                WorkingColorSpace = this.WorkingColorSpace,
                ColorTargeting = this.ColorTargeting?.Clone(),
                SplitToning = this.SplitToning?.Clone(),
                ColorGrading = this.ColorGrading?.Clone(),
                CreatedDate = this.CreatedDate,
                ModifiedDate = DateTime.Now;
            };
        }

        public string Serialize()
        {
            // JSON serileştirme için placeholder;
            return System.Text.Json.JsonSerializer.Serialize(this);
        }

        public static CorrectionProfile Deserialize(string data)
        {
            return System.Text.Json.JsonSerializer.Deserialize<CorrectionProfile>(data);
        }
    }

    public class ChannelAdjustments;
    {
        public float Brightness { get; set; }
        public float Contrast { get; set; }
        public float Gamma { get; set; } = 1.0f;
        public float Saturation { get; set; }
        public float Hue { get; set; }

        public ChannelAdjustments Clone() => new ChannelAdjustments;
        {
            Brightness = this.Brightness,
            Contrast = this.Contrast,
            Gamma = this.Gamma,
            Saturation = this.Saturation,
            Hue = this.Hue;
        };
    }

    public class ToneCurve;
    {
        public List<ControlPoint> ControlPoints { get; set; } = new List<ControlPoint>();
        public InterpolationType Interpolation { get; set; } = InterpolationType.Cubic;

        public byte Apply(byte value)
        {
            // Curve uygulama mantığı;
            return value;
        }

        public ToneCurve Clone() => new ToneCurve;
        {
            ControlPoints = this.ControlPoints.Select(p => p.Clone()).ToList(),
            Interpolation = this.Interpolation;
        };
    }

    public class ControlPoint;
    {
        public float Input { get; set; }
        public float Output { get; set; }

        public ControlPoint Clone() => new ControlPoint;
        {
            Input = this.Input,
            Output = this.Output;
        };
    }

    public enum InterpolationType;
    {
        Linear,
        Cubic,
        Bezier,
        Hermite;
    }

    public class ColorRange;
    {
        public float HueMin { get; set; }
        public float HueMax { get; set; }
        public float SaturationMin { get; set; }
        public float SaturationMax { get; set; }
        public float LuminanceMin { get; set; }
        public float LuminanceMax { get; set; }
    }

    public class ColorAdjustment;
    {
        public float HueShift { get; set; }
        public float SaturationAdjust { get; set; }
        public float LuminanceAdjust { get; set; }
    }

    public class ColorTargeting;
    {
        public ColorRange TargetColor { get; set; }
        public ColorAdjustment Adjustment { get; set; }
        public float Range { get; set; }

        public ColorTargeting Clone() => new ColorTargeting;
        {
            TargetColor = this.TargetColor,
            Adjustment = this.Adjustment,
            Range = this.Range;
        };
    }

    public class Toning;
    {
        public float Hue { get; set; }
        public float Saturation { get; set; }
        public float Balance { get; set; }
    }

    public class SplitToning;
    {
        public Toning Highlights { get; set; }
        public Toning Shadows { get; set; }
        public float Balance { get; set; }

        public SplitToning Clone() => new SplitToning;
        {
            Highlights = this.Highlights,
            Shadows = this.Shadows,
            Balance = this.Balance;
        };
    }

    public class LookupTable;
    {
        public byte[] Data { get; set; }
        public int Size { get; set; }
    }

    public class ColorGrading;
    {
        public LookupTable LookupTable { get; set; }
        public float Intensity { get; set; } = 1.0f;
    }

    public class AutoCorrectionOptions;
    {
        public bool AdjustExposure { get; set; } = true;
        public bool AdjustHighlightsShadows { get; set; } = true;
        public bool UseVibrance { get; set; } = true;
        public bool PreserveSkinTones { get; set; } = true;
        public float MaximumAdjustment { get; set; } = 0.5f;
    }

    public class HistogramAnalysis;
    {
        public int[] RedChannel { get; set; } = new int[256];
        public int[] GreenChannel { get; set; } = new int[256];
        public int[] BlueChannel { get; set; } = new int[256];
        public int[] Luminance { get; set; } = new int[256];
        public float RedMean { get; set; }
        public float GreenMean { get; set; }
        public float BlueMean { get; set; }
        public float LuminanceMean { get; set; }
        public float LuminanceStdDev { get; set; }
        public float LuminanceMin { get; set; }
        public float LuminanceMax { get; set; }
        public float SaturationMean { get; set; }
        public float SaturationStdDev { get; set; }
    }

    public class ColorBalanceAnalysis;
    {
        public float RedPercentage { get; set; }
        public float GreenPercentage { get; set; }
        public float BluePercentage { get; set; }
        public bool IsBalanced { get; set; }
        public float ImbalanceFactor { get; set; }
    }

    public class CorrectionSuggestions;
    {
        public float BrightnessCorrection { get; set; }
        public float ContrastCorrection { get; set; }
        public float SaturationCorrection { get; set; }
        public float TemperatureCorrection { get; set; }
        public float TintCorrection { get; set; }
        public float ExposureCorrection { get; set; }
        public float HighlightsCorrection { get; set; }
        public float ShadowsCorrection { get; set; }
        public float VibranceCorrection { get; set; }
        public float ClarityCorrection { get; set; }
        public float DehazeCorrection { get; set; }
    }

    public class ColorCorrectionCache;
    {
        private readonly Dictionary<string, ImageData> _cache;
        private readonly int _maxSizeMB;
        private long _currentSizeBytes;

        public ColorCorrectionCache(int maxSizeMB)
        {
            _maxSizeMB = maxSizeMB;
            _cache = new Dictionary<string, ImageData>();
        }

        public bool TryGet(string key, out ImageData image)
        {
            return _cache.TryGetValue(key, out image);
        }

        public void Add(string key, ImageData image)
        {
            var size = image.Data.Length;

            // Eski öğeleri temizle;
            while (_currentSizeBytes + size > _maxSizeMB * 1024L * 1024L && _cache.Count > 0)
            {
                var oldestKey = _cache.Keys.First();
                _currentSizeBytes -= _cache[oldestKey].Data.Length;
                _cache.Remove(oldestKey);
            }

            _cache[key] = image;
            _currentSizeBytes += size;
        }

        public void Clear()
        {
            _cache.Clear();
            _currentSizeBytes = 0;
        }
    }

    #endregion;
}
