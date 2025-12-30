using NEDA.AI.ComputerVision;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.GameDesign.GameplayDesign.MechanicsDesign;
using NEDA.GameDesign.UX_UI_Design.Configuration;
using NEDA.GameDesign.UX_UI_Design.DataModels;
using NEDA.GameDesign.UX_UI_Design.Export;
using NEDA.GameDesign.UX_UI_Design.Validation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace NEDA.GameDesign.UX_UI_Design.InterfaceDesign;
{
    /// <summary>
    /// UI/UX tasarım kılavuzu yönetimi ve uygulamasını sağlayan ana sınıf.
    /// Endüstriyel seviyede tasarım sistemi ve stil yönetimi sağlar.
    /// </summary>
    public class StyleGuide : IStyleGuide, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly IStyleValidator _styleValidator;
        private readonly IStyleExporter _styleExporter;
        private readonly StyleGuideConfiguration _configuration;

        private readonly Dictionary<string, DesignToken> _designTokens;
        private readonly Dictionary<string, ColorPalette> _colorPalettes;
        private readonly Dictionary<string, TypographyScale> _typographyScales;
        private readonly Dictionary<string, ComponentStyle> _componentStyles;
        private readonly Dictionary<string, DesignSystem> _designSystems;

        private DesignSystem _activeDesignSystem;
        private DesignContext _currentContext;
        private bool _isInitialized;
        private bool _isApplyingStyles;
        private readonly object _styleLock = new object();

        /// <summary>
        /// Tasarım token'ları;
        /// </summary>
        public IReadOnlyDictionary<string, DesignToken> DesignTokens => _designTokens;

        /// <summary>
        /// Renk paletleri;
        /// </summary>
        public IReadOnlyDictionary<string, ColorPalette> ColorPalettes => _colorPalettes;

        /// <summary>
        /// Tipografi ölçekleri;
        /// </summary>
        public IReadOnlyDictionary<string, TypographyScale> TypographyScales => _typographyScales;

        /// <summary>
        /// Bileşen stilleri;
        /// </summary>
        public IReadOnlyDictionary<string, ComponentStyle> ComponentStyles => _componentStyles;

        /// <summary>
        /// Tasarım sistemleri;
        /// </summary>
        public IReadOnlyDictionary<string, DesignSystem> DesignSystems => _designSystems;

        /// <summary>
        /// Aktif tasarım sistemi;
        /// </summary>
        public DesignSystem ActiveDesignSystem;
        {
            get => _activeDesignSystem;
            set;
            {
                if (_activeDesignSystem != value)
                {
                    var oldSystem = _activeDesignSystem;
                    _activeDesignSystem = value;
                    OnDesignSystemChanged(new DesignSystemChangedEventArgs(oldSystem, value));
                }
            }
        }

        /// <summary>
        /// Mevcut tasarım bağlamı;
        /// </summary>
        public DesignContext CurrentContext;
        {
            get => _currentContext;
            set;
            {
                if (_currentContext != value)
                {
                    var oldContext = _currentContext;
                    _currentContext = value;
                    OnDesignContextChanged(new DesignContextChangedEventArgs(oldContext, value));
                }
            }
        }

        /// <summary>
        /// Stil kılavuzu istatistikleri;
        /// </summary>
        public StyleGuideStatistics Statistics { get; private set; }

        /// <summary>
        /// Stil kılavuzu sürümü;
        /// </summary>
        public string Version => _configuration.Version;

        #endregion;

        #region Events;

        /// <summary>
        /// Stil kılavuzu başlatıldığında tetiklenir;
        /// </summary>
        public event EventHandler<StyleGuideInitializedEventArgs> StyleGuideInitialized;

        /// <summary>
        /// Tasarım sistemi değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<DesignSystemChangedEventArgs> DesignSystemChanged;

        /// <summary>
        /// Tasarım bağlamı değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<DesignContextChangedEventArgs> DesignContextChanged;

        /// <summary>
        /// Tasarım token'ı eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<DesignTokenEventArgs> DesignTokenAdded;

        /// <summary>
        /// Renk paleti eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ColorPaletteEventArgs> ColorPaletteAdded;

        /// <summary>
        /// Bileşen stili eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<ComponentStyleEventArgs> ComponentStyleAdded;

        /// <summary>
        /// Stil uygulandığında tetiklenir;
        /// </summary>
        public event EventHandler<StyleAppliedEventArgs> StyleApplied;

        /// <summary>
        /// Stil doğrulandığında tetiklenir;
        /// </summary>
        public event EventHandler<StyleValidatedEventArgs> StyleValidated;

        #endregion;

        #region Constructor;

        /// <summary>
        /// StyleGuide sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="styleValidator">Stil doğrulayıcı</param>
        /// <param name="styleExporter">Stil dışa aktarıcı</param>
        /// <param name="configuration">Stil kılavuzu konfigürasyonu</param>
        public StyleGuide(
            ILogger logger,
            IStyleValidator styleValidator,
            IStyleExporter styleExporter,
            StyleGuideConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _styleValidator = styleValidator ?? throw new ArgumentNullException(nameof(styleValidator));
            _styleExporter = styleExporter ?? throw new ArgumentNullException(nameof(styleExporter));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _designTokens = new Dictionary<string, DesignToken>();
            _colorPalettes = new Dictionary<string, ColorPalette>();
            _typographyScales = new Dictionary<string, TypographyScale>();
            _componentStyles = new Dictionary<string, ComponentStyle>();
            _designSystems = new Dictionary<string, DesignSystem>();

            Initialize();
        }

        #endregion;

        #region Initialization;

        private void Initialize()
        {
            try
            {
                _logger.LogInformation("StyleGuide başlatılıyor...");

                _currentContext = DesignContext.Default;
                Statistics = new StyleGuideStatistics();

                // Konfigürasyon validasyonu;
                ValidateConfiguration(_configuration);

                // Stil doğrulayıcıyı başlat;
                InitializeStyleValidator();

                // Stil dışa aktarıcıyı başlat;
                InitializeStyleExporter();

                // Varsayılan tasarım token'larını yükle;
                LoadDefaultDesignTokens();

                // Varsayılan renk paletlerini yükle;
                LoadDefaultColorPalettes();

                // Varsayılan tipografi ölçeklerini yükle;
                LoadDefaultTypographyScales();

                // Varsayılan bileşen stillerini yükle;
                LoadDefaultComponentStyles();

                // Varsayılan tasarım sistemlerini yükle;
                LoadDefaultDesignSystems();

                // Aktif tasarım sistemini ayarla;
                SetActiveDesignSystem(_configuration.DefaultDesignSystem);

                _isInitialized = true;

                OnStyleGuideInitialized(new StyleGuideInitializedEventArgs;
                {
                    Timestamp = DateTime.UtcNow,
                    DesignTokenCount = _designTokens.Count,
                    ColorPaletteCount = _colorPalettes.Count,
                    ComponentStyleCount = _componentStyles.Count,
                    Version = _configuration.Version;
                });

                _logger.LogInformation("StyleGuide başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "StyleGuide başlatma sırasında hata oluştu.");
                throw new StyleGuideException("StyleGuide başlatılamadı.", ex);
            }
        }

        private void ValidateConfiguration(StyleGuideConfiguration config)
        {
            if (string.IsNullOrEmpty(config.Version))
                throw new ArgumentException("Stil kılavuzu sürümü belirtilmelidir.");

            if (string.IsNullOrEmpty(config.DefaultDesignSystem))
                throw new ArgumentException("Varsayılan tasarım sistemi belirtilmelidir.");

            if (config.MinContrastRatio <= 0)
                throw new ArgumentException("Minimum kontrast oranı pozitif olmalıdır.");
        }

        private void InitializeStyleValidator()
        {
            _styleValidator.Initialize(_configuration.ValidationSettings);
        }

        private void InitializeStyleExporter()
        {
            _styleExporter.Initialize(_configuration.ExportSettings);
        }

        private void LoadDefaultDesignTokens()
        {
            // Spacing token'ları;
            var spacingTokens = new[]
            {
                new DesignToken;
                {
                    Id = "spacing-xs",
                    Name = "Extra Small Spacing",
                    Category = TokenCategory.Spacing,
                    Value = "4px",
                    Description = "Çok küçük boşluk",
                    Usage = "İç içe elemanlar arasında"
                },
                new DesignToken;
                {
                    Id = "spacing-sm",
                    Name = "Small Spacing",
                    Category = TokenCategory.Spacing,
                    Value = "8px",
                    Description = "Küçük boşluk",
                    Usage = "Yakın ilişkili elemanlar arasında"
                },
                new DesignToken;
                {
                    Id = "spacing-md",
                    Name = "Medium Spacing",
                    Category = TokenCategory.Spacing,
                    Value = "16px",
                    Description = "Orta boşluk",
                    Usage = "Standart eleman aralıkları"
                },
                new DesignToken;
                {
                    Id = "spacing-lg",
                    Name = "Large Spacing",
                    Category = TokenCategory.Spacing,
                    Value = "24px",
                    Description = "Büyük boşluk",
                    Usage = "Grup elemanları arasında"
                },
                new DesignToken;
                {
                    Id = "spacing-xl",
                    Name = "Extra Large Spacing",
                    Category = TokenCategory.Spacing,
                    Value = "32px",
                    Description = "Çok büyük boşluk",
                    Usage = "Bölümler arasında"
                }
            };

            // Border radius token'ları;
            var borderRadiusTokens = new[]
            {
                new DesignToken;
                {
                    Id = "radius-sm",
                    Name = "Small Border Radius",
                    Category = TokenCategory.Border,
                    Value = "4px",
                    Description = "Küçük köşe yuvarlama",
                    Usage = "Input alanları, küçük butonlar"
                },
                new DesignToken;
                {
                    Id = "radius-md",
                    Name = "Medium Border Radius",
                    Category = TokenCategory.Border,
                    Value = "8px",
                    Description = "Orta köşe yuvarlama",
                    Usage = "Standart butonlar, kartlar"
                },
                new DesignToken;
                {
                    Id = "radius-lg",
                    Name = "Large Border Radius",
                    Category = TokenCategory.Border,
                    Value = "16px",
                    Description = "Büyük köşe yuvarlama",
                    Usage = "Büyük kartlar, modallar"
                },
                new DesignToken;
                {
                    Id = "radius-full",
                    Name = "Full Border Radius",
                    Category = TokenCategory.Border,
                    Value = "9999px",
                    Description = "Tam yuvarlak",
                    Usage = "Avatar, yuvarlak butonlar"
                }
            };

            // Shadow token'ları;
            var shadowTokens = new[]
            {
                new DesignToken;
                {
                    Id = "shadow-sm",
                    Name = "Small Shadow",
                    Category = TokenCategory.Shadow,
                    Value = "0 1px 2px 0 rgba(0, 0, 0, 0.05)",
                    Description = "Küçük gölge",
                    Usage = "Hafif yükseltilmiş elemanlar"
                },
                new DesignToken;
                {
                    Id = "shadow-md",
                    Name = "Medium Shadow",
                    Category = TokenCategory.Shadow,
                    Value = "0 4px 6px -1px rgba(0, 0, 0, 0.1), 0 2px 4px -1px rgba(0, 0, 0, 0.06)",
                    Description = "Orta gölge",
                    Usage = "Kartlar, dropdown'lar"
                },
                new DesignToken;
                {
                    Id = "shadow-lg",
                    Name = "Large Shadow",
                    Category = TokenCategory.Shadow,
                    Value = "0 10px 15px -3px rgba(0, 0, 0, 0.1), 0 4px 6px -2px rgba(0, 0, 0, 0.05)",
                    Description = "Büyük gölge",
                    Usage = "Modallar, popup'lar"
                }
            };

            // Tüm token'ları ekle;
            foreach (var token in spacingTokens.Concat(borderRadiusTokens).Concat(shadowTokens))
            {
                AddDesignTokenInternal(token);
            }
        }

        private void LoadDefaultColorPalettes()
        {
            // Birincil renk paleti;
            var primaryPalette = new ColorPalette;
            {
                Id = "primary",
                Name = "Primary Colors",
                Description = "Birincil renk paleti",
                Colors = new Dictionary<string, ColorDefinition>
                {
                    { "50", new ColorDefinition { Name = "Primary 50", Hex = "#EFF6FF", RGB = "239, 246, 255" } },
                    { "100", new ColorDefinition { Name = "Primary 100", Hex = "#DBEAFE", RGB = "219, 234, 254" } },
                    { "200", new ColorDefinition { Name = "Primary 200", Hex = "#BFDBFE", RGB = "191, 219, 254" } },
                    { "300", new ColorDefinition { Name = "Primary 300", Hex = "#93C5FD", RGB = "147, 197, 253" } },
                    { "400", new ColorDefinition { Name = "Primary 400", Hex = "#60A5FA", RGB = "96, 165, 250" } },
                    { "500", new ColorDefinition { Name = "Primary 500", Hex = "#3B82F6", RGB = "59, 130, 246" } },
                    { "600", new ColorDefinition { Name = "Primary 600", Hex = "#2563EB", RGB = "37, 99, 235" } },
                    { "700", new ColorDefinition { Name = "Primary 700", Hex = "#1D4ED8", RGB = "29, 78, 216" } },
                    { "800", new ColorDefinition { Name = "Primary 800", Hex = "#1E40AF", RGB = "30, 64, 175" } },
                    { "900", new ColorDefinition { Name = "Primary 900", Hex = "#1E3A8A", RGB = "30, 58, 138" } }
                },
                BaseColor = "#3B82F6"
            };

            // Nötr renk paleti;
            var neutralPalette = new ColorPalette;
            {
                Id = "neutral",
                Name = "Neutral Colors",
                Description = "Nötr renk paleti",
                Colors = new Dictionary<string, ColorDefinition>
                {
                    { "50", new ColorDefinition { Name = "Neutral 50", Hex = "#FAFAFA", RGB = "250, 250, 250" } },
                    { "100", new ColorDefinition { Name = "Neutral 100", Hex = "#F5F5F5", RGB = "245, 245, 245" } },
                    { "200", new ColorDefinition { Name = "Neutral 200", Hex = "#E5E5E5", RGB = "229, 229, 229" } },
                    { "300", new ColorDefinition { Name = "Neutral 300", Hex = "#D4D4D4", RGB = "212, 212, 212" } },
                    { "400", new ColorDefinition { Name = "Neutral 400", Hex = "#A3A3A3", RGB = "163, 163, 163" } },
                    { "500", new ColorDefinition { Name = "Neutral 500", Hex = "#737373", RGB = "115, 115, 115" } },
                    { "600", new ColorDefinition { Name = "Neutral 600", Hex = "#525252", RGB = "82, 82, 82" } },
                    { "700", new ColorDefinition { Name = "Neutral 700", Hex = "#404040", RGB = "64, 64, 64" } },
                    { "800", new ColorDefinition { Name = "Neutral 800", Hex = "#262626", RGB = "38, 38, 38" } },
                    { "900", new ColorDefinition { Name = "Neutral 900", Hex = "#171717", RGB = "23, 23, 23" } }
                },
                BaseColor = "#737373"
            };

            // Durum renk paletleri;
            var successPalette = new ColorPalette;
            {
                Id = "success",
                Name = "Success Colors",
                Description = "Başarı durumu renk paleti",
                Colors = new Dictionary<string, ColorDefinition>
                {
                    { "500", new ColorDefinition { Name = "Success 500", Hex = "#10B981", RGB = "16, 185, 129" } },
                    { "600", new ColorDefinition { Name = "Success 600", Hex = "#059669", RGB = "5, 150, 105" } },
                    { "700", new ColorDefinition { Name = "Success 700", Hex = "#047857", RGB = "4, 120, 87" } }
                },
                BaseColor = "#10B981"
            };

            var errorPalette = new ColorPalette;
            {
                Id = "error",
                Name = "Error Colors",
                Description = "Hata durumu renk paleti",
                Colors = new Dictionary<string, ColorDefinition>
                {
                    { "500", new ColorDefinition { Name = "Error 500", Hex = "#EF4444", RGB = "239, 68, 68" } },
                    { "600", new ColorDefinition { Name = "Error 600", Hex = "#DC2626", RGB = "220, 38, 38" } },
                    { "700", new ColorDefinition { Name = "Error 700", Hex = "#B91C1C", RGB = "185, 28, 28" } }
                },
                BaseColor = "#EF4444"
            };

            var warningPalette = new ColorPalette;
            {
                Id = "warning",
                Name = "Warning Colors",
                Description = "Uyarı durumu renk paleti",
                Colors = new Dictionary<string, ColorDefinition>
                {
                    { "500", new ColorDefinition { Name = "Warning 500", Hex = "#F59E0B", RGB = "245, 158, 11" } },
                    { "600", new ColorDefinition { Name = "Warning 600", Hex = "#D97706", RGB = "217, 119, 6" } },
                    { "700", new ColorDefinition { Name = "Warning 700", Hex = "#B45309", RGB = "180, 83, 9" } }
                },
                BaseColor = "#F59E0B"
            };

            // Renk paletlerini ekle;
            var palettes = new[] { primaryPalette, neutralPalette, successPalette, errorPalette, warningPalette };
            foreach (var palette in palettes)
            {
                AddColorPaletteInternal(palette);
            }
        }

        private void LoadDefaultTypographyScales()
        {
            // Başlık ölçeği;
            var headingScale = new TypographyScale;
            {
                Id = "headings",
                Name = "Heading Scale",
                Description = "Başlık tipografi ölçeği",
                FontFamily = "Inter, system-ui, sans-serif",
                FontWeights = new Dictionary<string, int>
                {
                    { "light", 300 },
                    { "regular", 400 },
                    { "medium", 500 },
                    { "semibold", 600 },
                    { "bold", 700 }
                },
                Sizes = new Dictionary<string, TypographySize>
                {
                    { "h1", new TypographySize { Name = "Heading 1", Size = "48px", LineHeight = "56px", LetterSpacing = "-0.02em" } },
                    { "h2", new TypographySize { Name = "Heading 2", Size = "36px", LineHeight = "44px", LetterSpacing = "-0.01em" } },
                    { "h3", new TypographySize { Name = "Heading 3", Size = "30px", LineHeight = "36px", LetterSpacing = "0em" } },
                    { "h4", new TypographySize { Name = "Heading 4", Size = "24px", LineHeight = "32px", LetterSpacing = "0em" } },
                    { "h5", new TypographySize { Name = "Heading 5", Size = "20px", LineHeight = "28px", LetterSpacing = "0em" } },
                    { "h6", new TypographySize { Name = "Heading 6", Size = "16px", LineHeight = "24px", LetterSpacing = "0em" } }
                }
            };

            // Gövde metni ölçeği;
            var bodyScale = new TypographyScale;
            {
                Id = "body",
                Name = "Body Text Scale",
                Description = "Gövde metni tipografi ölçeği",
                FontFamily = "Inter, system-ui, sans-serif",
                FontWeights = new Dictionary<string, int>
                {
                    { "regular", 400 },
                    { "medium", 500 }
                },
                Sizes = new Dictionary<string, TypographySize>
                {
                    { "xl", new TypographySize { Name = "Extra Large", Size = "20px", LineHeight = "28px", LetterSpacing = "0em" } },
                    { "lg", new TypographySize { Name = "Large", Size = "18px", LineHeight = "28px", LetterSpacing = "0em" } },
                    { "md", new TypographySize { Name = "Medium", Size = "16px", LineHeight = "24px", LetterSpacing = "0em" } },
                    { "sm", new TypographySize { Name = "Small", Size = "14px", LineHeight = "20px", LetterSpacing = "0em" } },
                    { "xs", new TypographySize { Name = "Extra Small", Size = "12px", LineHeight = "16px", LetterSpacing = "0em" } }
                }
            };

            // Ölçekleri ekle;
            AddTypographyScaleInternal(headingScale);
            AddTypographyScaleInternal(bodyScale);
        }

        private void LoadDefaultComponentStyles()
        {
            // Buton stilleri;
            var buttonStyles = new[]
            {
                new ComponentStyle;
                {
                    Id = "button-primary",
                    Name = "Primary Button",
                    ComponentType = ComponentType.Button,
                    Variant = "primary",
                    Properties = new Dictionary<string, string>
                    {
                        { "backgroundColor", "primary-500" },
                        { "color", "white" },
                        { "padding", "spacing-md spacing-lg" },
                        { "borderRadius", "radius-md" },
                        { "fontSize", "body-md" },
                        { "fontWeight", "medium" },
                        { "border", "none" },
                        { "cursor", "pointer" }
                    },
                    States = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "hover", new Dictionary<string, string> { { "backgroundColor", "primary-600" } } },
                        { "active", new Dictionary<string, string> { { "backgroundColor", "primary-700" } } },
                        { "disabled", new Dictionary<string, string> { { "opacity", "0.5" }, { "cursor", "not-allowed" } } }
                    }
                },
                new ComponentStyle;
                {
                    Id = "button-secondary",
                    Name = "Secondary Button",
                    ComponentType = ComponentType.Button,
                    Variant = "secondary",
                    Properties = new Dictionary<string, string>
                    {
                        { "backgroundColor", "transparent" },
                        { "color", "primary-500" },
                        { "padding", "spacing-md spacing-lg" },
                        { "borderRadius", "radius-md" },
                        { "fontSize", "body-md" },
                        { "fontWeight", "medium" },
                        { "border", "2px solid primary-500" },
                        { "cursor", "pointer" }
                    },
                    States = new Dictionary<string, Dictionary<string, string>>
                    {
                        { "hover", new Dictionary<string, string> { { "backgroundColor", "primary-50" } } },
                        { "active", new Dictionary<string, string> { { "backgroundColor", "primary-100" } } }
                    }
                }
            };

            // Input stilleri;
            var inputStyle = new ComponentStyle;
            {
                Id = "input-default",
                Name = "Default Input",
                ComponentType = ComponentType.Input,
                Variant = "default",
                Properties = new Dictionary<string, string>
                {
                    { "backgroundColor", "white" },
                    { "color", "neutral-900" },
                    { "padding", "spacing-sm spacing-md" },
                    { "borderRadius", "radius-sm" },
                    { "fontSize", "body-md" },
                    { "border", "1px solid neutral-300" },
                    { "outline", "none" }
                },
                States = new Dictionary<string, Dictionary<string, string>>
                {
                    { "focus", new Dictionary<string, string> { { "borderColor", "primary-500" }, { "boxShadow", "shadow-sm primary-100" } } },
                    { "error", new Dictionary<string, string> { { "borderColor", "error-500" }, { "color", "error-700" } } },
                    { "disabled", new Dictionary<string, string> { { "backgroundColor", "neutral-100" }, { "color", "neutral-500" } } }
                }
            };

            // Kart stili;
            var cardStyle = new ComponentStyle;
            {
                Id = "card-default",
                Name = "Default Card",
                ComponentType = ComponentType.Card,
                Variant = "default",
                Properties = new Dictionary<string, string>
                {
                    { "backgroundColor", "white" },
                    { "borderRadius", "radius-lg" },
                    { "padding", "spacing-lg" },
                    { "boxShadow", "shadow-md" },
                    { "border", "1px solid neutral-200" }
                },
                Variants = new Dictionary<string, Dictionary<string, string>>
                {
                    { "elevated", new Dictionary<string, string> { { "boxShadow", "shadow-lg" }, { "border", "none" } } },
                    { "outlined", new Dictionary<string, string> { { "boxShadow", "none" }, { "border", "1px solid neutral-300" } } }
                }
            };

            // Tüm bileşen stillerini ekle;
            foreach (var style in buttonStyles.Concat(new[] { inputStyle, cardStyle }))
            {
                AddComponentStyleInternal(style);
            }
        }

        private void LoadDefaultDesignSystems()
        {
            // Modern tasarım sistemi;
            var modernSystem = new DesignSystem;
            {
                Id = "modern",
                Name = "Modern Design System",
                Description = "Modern ve minimal tasarım sistemi",
                Version = "1.0.0",
                BaseTokens = new List<string>
                {
                    "spacing-xs", "spacing-sm", "spacing-md", "spacing-lg", "spacing-xl",
                    "radius-sm", "radius-md", "radius-lg", "radius-full",
                    "shadow-sm", "shadow-md", "shadow-lg"
                },
                ColorPalettes = new List<string> { "primary", "neutral", "success", "error", "warning" },
                TypographyScales = new List<string> { "headings", "body" },
                ComponentStyles = new List<string> { "button-primary", "button-secondary", "input-default", "card-default" },
                Guidelines = new DesignGuidelines;
                {
                    SpacingUnit = "4px",
                    GridColumns = 12,
                    GridGutter = "spacing-md",
                    Breakpoints = new Dictionary<string, string>
                    {
                        { "sm", "640px" },
                        { "md", "768px" },
                        { "lg", "1024px" },
                        { "xl", "1280px" },
                        { "2xl", "1536px" }
                    },
                    AccessibilityStandards = new AccessibilityStandards;
                    {
                        MinContrastRatio = 4.5,
                        MinTouchTargetSize = "44px",
                        SupportsScreenReaders = true,
                        ColorBlindFriendly = true;
                    }
                }
            };

            // Oyun tasarım sistemi;
            var gameSystem = new DesignSystem;
            {
                Id = "game",
                Name = "Game UI Design System",
                Description = "Oyun kullanıcı arayüzü için tasarım sistemi",
                Version = "1.0.0",
                BaseTokens = new List<string>
                {
                    "spacing-sm", "spacing-md", "spacing-lg", "spacing-xl",
                    "radius-md", "radius-lg",
                    "shadow-lg"
                },
                ColorPalettes = new List<string> { "primary", "neutral", "error", "warning" },
                TypographyScales = new List<string> { "headings", "body" },
                ComponentStyles = new List<string> { "button-primary", "button-secondary", "card-default" },
                Guidelines = new DesignGuidelines;
                {
                    SpacingUnit = "8px",
                    GridColumns = 8,
                    GridGutter = "spacing-lg",
                    Breakpoints = new Dictionary<string, string>
                    {
                        { "mobile", "320px" },
                        { "tablet", "768px" },
                        { "desktop", "1024px" },
                        { "hd", "1920px" }
                    },
                    AccessibilityStandards = new AccessibilityStandards;
                    {
                        MinContrastRatio = 3.0,
                        MinTouchTargetSize = "48px",
                        SupportsScreenReaders = true,
                        ColorBlindFriendly = true;
                    }
                }
            };

            // Tasarım sistemlerini ekle;
            AddDesignSystemInternal(modernSystem);
            AddDesignSystemInternal(gameSystem);
        }

        private void SetActiveDesignSystem(string systemId)
        {
            if (!_designSystems.ContainsKey(systemId))
                throw new StyleGuideException($"Tasarım sistemi bulunamadı: {systemId}");

            ActiveDesignSystem = _designSystems[systemId];
            _logger.LogInformation($"Aktif tasarım sistemi ayarlandı: {systemId}");
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Tasarım token'ı ekler;
        /// </summary>
        /// <param name="token">Tasarım token'ı</param>
        public void AddDesignToken(DesignToken token)
        {
            if (token == null)
                throw new ArgumentNullException(nameof(token));

            if (_designTokens.ContainsKey(token.Id))
                throw new StyleGuideException($"Tasarım token'ı zaten mevcut: {token.Id}");

            try
            {
                // Token'ı doğrula;
                var validationResult = _styleValidator.ValidateDesignToken(token);
                if (!validationResult.IsValid)
                {
                    throw new StyleGuideException($"Tasarım token'ı geçersiz: {string.Join(", ", validationResult.Errors)}");
                }

                AddDesignTokenInternal(token);

                OnDesignTokenAdded(new DesignTokenEventArgs;
                {
                    TokenId = token.Id,
                    Category = token.Category,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Tasarım token'ı eklendi: {token.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Tasarım token'ı ekleme sırasında hata oluştu: {token.Id}");
                throw new StyleGuideException($"Tasarım token'ı eklenemedi: {token.Id}", ex);
            }
        }

        /// <summary>
        /// Renk paleti ekler;
        /// </summary>
        /// <param name="palette">Renk paleti</param>
        public void AddColorPalette(ColorPalette palette)
        {
            if (palette == null)
                throw new ArgumentNullException(nameof(palette));

            if (_colorPalettes.ContainsKey(palette.Id))
                throw new StyleGuideException($"Renk paleti zaten mevcut: {palette.Id}");

            try
            {
                // Renk paletini doğrula;
                var validationResult = _styleValidator.ValidateColorPalette(palette);
                if (!validationResult.IsValid)
                {
                    throw new StyleGuideException($"Renk paleti geçersiz: {string.Join(", ", validationResult.Errors)}");
                }

                AddColorPaletteInternal(palette);

                OnColorPaletteAdded(new ColorPaletteEventArgs;
                {
                    PaletteId = palette.Id,
                    ColorCount = palette.Colors.Count,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Renk paleti eklendi: {palette.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Renk paleti ekleme sırasında hata oluştu: {palette.Id}");
                throw new StyleGuideException($"Renk paleti eklenemedi: {palette.Id}", ex);
            }
        }

        /// <summary>
        /// Bileşen stili ekler;
        /// </summary>
        /// <param name="style">Bileşen stili</param>
        public void AddComponentStyle(ComponentStyle style)
        {
            if (style == null)
                throw new ArgumentNullException(nameof(style));

            if (_componentStyles.ContainsKey(style.Id))
                throw new StyleGuideException($"Bileşen stili zaten mevcut: {style.Id}");

            try
            {
                // Bileşen stilini doğrula;
                var validationResult = _styleValidator.ValidateComponentStyle(style);
                if (!validationResult.IsValid)
                {
                    throw new StyleGuideException($"Bileşen stili geçersiz: {string.Join(", ", validationResult.Errors)}");
                }

                AddComponentStyleInternal(style);

                OnComponentStyleAdded(new ComponentStyleEventArgs;
                {
                    StyleId = style.Id,
                    ComponentType = style.ComponentType,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Bileşen stili eklendi: {style.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Bileşen stili ekleme sırasında hata oluştu: {style.Id}");
                throw new StyleGuideException($"Bileşen stili eklenemedi: {style.Id}", ex);
            }
        }

        /// <summary>
        /// Tasarım sistemi ekler;
        /// </summary>
        /// <param name="system">Tasarım sistemi</param>
        public void AddDesignSystem(DesignSystem system)
        {
            if (system == null)
                throw new ArgumentNullException(nameof(system));

            if (_designSystems.ContainsKey(system.Id))
                throw new StyleGuideException($"Tasarım sistemi zaten mevcut: {system.Id}");

            try
            {
                // Tasarım sistemini doğrula;
                var validationResult = _styleValidator.ValidateDesignSystem(system, this);
                if (!validationResult.IsValid)
                {
                    throw new StyleGuideException($"Tasarım sistemi geçersiz: {string.Join(", ", validationResult.Errors)}");
                }

                AddDesignSystemInternal(system);

                _logger.LogInformation($"Tasarım sistemi eklendi: {system.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Tasarım sistemi ekleme sırasında hata oluştu: {system.Id}");
                throw new StyleGuideException($"Tasarım sistemi eklenemedi: {system.Id}", ex);
            }
        }

        /// <summary>
        /// Stili bir UI elementine uygular;
        /// </summary>
        /// <param name="element">UI elementi</param>
        /// <param name="styleId">Stil ID</param>
        /// <param name="variant">Varyant (opsiyonel)</param>
        /// <param name="state">Durum (opsiyonel)</param>
        public async Task ApplyStyleAsync(object element, string styleId, string variant = null, string state = null)
        {
            if (element == null)
                throw new ArgumentNullException(nameof(element));

            if (!_componentStyles.ContainsKey(styleId))
                throw new StyleGuideException($"Bileşen stili bulunamadı: {styleId}");

            lock (_styleLock)
            {
                if (_isApplyingStyles)
                    return;

                _isApplyingStyles = true;
            }

            try
            {
                var style = _componentStyles[styleId];
                var styleProperties = await ResolveStylePropertiesAsync(style, variant, state);

                // Stili uygula;
                await ApplyPropertiesToElementAsync(element, styleProperties);

                OnStyleApplied(new StyleAppliedEventArgs;
                {
                    ElementType = element.GetType().Name,
                    StyleId = styleId,
                    Variant = variant,
                    State = state,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogDebug($"Stil uygulandı: {styleId} -> {element.GetType().Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Stil uygulama sırasında hata oluştu: {styleId}");
                throw new StyleGuideException($"Stil uygulanamadı: {styleId}", ex);
            }
            finally
            {
                lock (_styleLock)
                {
                    _isApplyingStyles = false;
                }
            }
        }

        /// <summary>
        /// Tasarım token'ını çözümler;
        /// </summary>
        /// <param name="tokenReference">Token referansı</param>
        /// <returns>Çözümlenmiş değer</returns>
        public string ResolveToken(string tokenReference)
        {
            if (string.IsNullOrEmpty(tokenReference))
                return string.Empty;

            // Token referansını çözümle;
            if (tokenReference.StartsWith("$"))
            {
                var tokenId = tokenReference.Substring(1);
                if (_designTokens.TryGetValue(tokenId, out var token))
                {
                    return token.Value;
                }
            }
            // Renk referansını çözümle;
            else if (tokenReference.Contains("-"))
            {
                var parts = tokenReference.Split('-');
                if (parts.Length >= 2)
                {
                    var paletteId = parts[0];
                    var colorKey = parts[1];

                    if (_colorPalettes.TryGetValue(paletteId, out var palette))
                    {
                        if (palette.Colors.TryGetValue(colorKey, out var color))
                        {
                            return color.Hex;
                        }
                    }
                }
            }

            return tokenReference;
        }

        /// <summary>
        /// Tasarım token'larını doğrular;
        /// </summary>
        /// <param name="tokenId">Token ID (tüm token'lar için boş bırakın)</param>
        /// <returns>Doğrulama sonucu</returns>
        public async Task<ValidationResult> ValidateTokensAsync(string tokenId = null)
        {
            try
            {
                ValidationResult result;

                if (string.IsNullOrEmpty(tokenId))
                {
                    // Tüm token'ları doğrula;
                    result = await _styleValidator.ValidateAllTokensAsync(_designTokens.Values);
                }
                else;
                {
                    // Belirli bir token'ı doğrula;
                    if (!_designTokens.TryGetValue(tokenId, out var token))
                        throw new StyleGuideException($"Tasarım token'ı bulunamadı: {tokenId}");

                    result = _styleValidator.ValidateDesignToken(token);
                }

                OnStyleValidated(new StyleValidatedEventArgs;
                {
                    TokenId = tokenId,
                    Result = result,
                    Timestamp = DateTime.UtcNow;
                });

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Token doğrulama sırasında hata oluştu: {tokenId}");
                throw new StyleGuideException($"Token doğrulanamadı: {tokenId}", ex);
            }
        }

        /// <summary>
        /// Renk paletini doğrular;
        /// </summary>
        /// <param name="paletteId">Palet ID</param>
        /// <returns>Doğrulama sonucu</returns>
        public ValidationResult ValidateColorPalette(string paletteId)
        {
            if (!_colorPalettes.ContainsKey(paletteId))
                throw new StyleGuideException($"Renk paleti bulunamadı: {paletteId}");

            var palette = _colorPalettes[paletteId];
            var result = _styleValidator.ValidateColorPalette(palette);

            OnStyleValidated(new StyleValidatedEventArgs;
            {
                PaletteId = paletteId,
                Result = result,
                Timestamp = DateTime.UtcNow;
            });

            return result;
        }

        /// <summary>
        /// Erişilebilirlik kontrolleri yapar;
        /// </summary>
        /// <param name="foregroundColor">Ön plan rengi</param>
        /// <param name="backgroundColor">Arka plan rengi</param>
        /// <returns>Erişilebilirlik sonucu</returns>
        public AccessibilityResult CheckAccessibility(string foregroundColor, string backgroundColor)
        {
            try
            {
                var result = _styleValidator.CheckColorContrast(foregroundColor, backgroundColor);

                // Kontrast oranını aktif tasarım sistemine göre kontrol et;
                var minRatio = ActiveDesignSystem?.Guidelines.AccessibilityStandards.MinContrastRatio ?? 4.5;
                result.Passes = result.ContrastRatio >= minRatio;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erişilebilirlik kontrolü sırasında hata oluştu.");
                throw new StyleGuideException("Erişilebilirlik kontrolü başarısız.", ex);
            }
        }

        /// <summary>
        /// Stil kılavuzunu dışa aktarır;
        /// </summary>
        /// <param name="exportType">Dışa aktarma türü</param>
        /// <param name="options">Dışa aktarma seçenekleri</param>
        /// <returns>Dışa aktarılan veriler</returns>
        public async Task<StyleExportData> ExportStyleGuideAsync(StyleExportType exportType, ExportOptions options = null)
        {
            try
            {
                _logger.LogInformation($"Stil kılavuzu dışa aktarılıyor. Tip: {exportType}");

                var exportData = new StyleExportData;
                {
                    ExportId = Guid.NewGuid().ToString(),
                    ExportType = exportType,
                    ExportTime = DateTime.UtcNow,
                    DesignSystem = ActiveDesignSystem,
                    TokenCount = _designTokens.Count,
                    PaletteCount = _colorPalettes.Count;
                };

                switch (exportType)
                {
                    case StyleExportType.JSON:
                        exportData.Data = await _styleExporter.ExportToJsonAsync(this, options);
                        break;
                    case StyleExportType.CSS:
                        exportData.Data = await _styleExporter.ExportToCssAsync(this, options);
                        break;
                    case StyleExportType.XAML:
                        exportData.Data = await _styleExporter.ExportToXamlAsync(this, options);
                        break;
                    case StyleExportType.UNITY:
                        exportData.Data = await _styleExporter.ExportToUnityAsync(this, options);
                        break;
                    default:
                        throw new NotSupportedException($"Desteklenmeyen export tipi: {exportType}");
                }

                exportData.Success = true;
                exportData.Message = "Stil kılavuzu başarıyla dışa aktarıldı.";

                _logger.LogInformation($"Stil kılavuzu dışa aktarıldı. ID: {exportData.ExportId}");

                return exportData;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stil kılavuzu dışa aktarma sırasında hata oluştu.");
                throw new StyleGuideException("Stil kılavuzu dışa aktarılamadı.", ex);
            }
        }

        /// <summary>
        /// Stil kılavuzunu içe aktarır;
        /// </summary>
        /// <param name="importData">İçe aktarılacak veriler</param>
        /// <param name="importType">İçe aktarma türü</param>
        public async Task ImportStyleGuideAsync(byte[] importData, StyleImportType importType)
        {
            try
            {
                _logger.LogInformation($"Stil kılavuzu içe aktarılıyor. Tip: {importType}");

                // Mevcut verileri temizle;
                ClearStyleGuide();

                // İçe aktar;
                await _styleExporter.ImportAsync(importData, importType, this);

                _logger.LogInformation("Stil kılavuzu başarıyla içe aktarıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stil kılavuzu içe aktarma sırasında hata oluştu.");
                throw new StyleGuideException("Stil kılavuzu içe aktarılamadı.", ex);
            }
        }

        /// <summary>
        /// Tasarım token'ını bulur;
        /// </summary>
        /// <param name="tokenId">Token ID</param>
        /// <returns>Tasarım token'ı</returns>
        public DesignToken FindDesignToken(string tokenId)
        {
            return _designTokens.TryGetValue(tokenId, out var token) ? token : null;
        }

        /// <summary>
        /// Renk paletini bulur;
        /// </summary>
        /// <param name="paletteId">Palet ID</param>
        /// <returns>Renk paleti</returns>
        public ColorPalette FindColorPalette(string paletteId)
        {
            return _colorPalettes.TryGetValue(paletteId, out var palette) ? palette : null;
        }

        /// <summary>
        /// Bileşen stilini bulur;
        /// </summary>
        /// <param name="styleId">Stil ID</param>
        /// <returns>Bileşen stili</returns>
        public ComponentStyle FindComponentStyle(string styleId)
        {
            return _componentStyles.TryGetValue(styleId, out var style) ? style : null;
        }

        /// <summary>
        /// Tasarım token'larını kategorilere göre filtreler;
        /// </summary>
        /// <param name="category">Token kategorisi</param>
        /// <returns>Filtrelenmiş token'lar</returns>
        public IEnumerable<DesignToken> GetTokensByCategory(TokenCategory category)
        {
            return _designTokens.Values.Where(t => t.Category == category);
        }

        /// <summary>
        /// Bileşen stillerini türüne göre filtreler;
        /// </summary>
        /// <param name="componentType">Bileşen türü</param>
        /// <returns>Filtrelenmiş stiller</returns>
        public IEnumerable<ComponentStyle> GetStylesByComponentType(ComponentType componentType)
        {
            return _componentStyles.Values.Where(s => s.ComponentType == componentType);
        }

        /// <summary>
        /// Stil kılavuzunu temizler;
        /// </summary>
        public void ClearStyleGuide()
        {
            try
            {
                _logger.LogInformation("Stil kılavuzu temizleniyor...");

                _designTokens.Clear();
                _colorPalettes.Clear();
                _typographyScales.Clear();
                _componentStyles.Clear();
                _designSystems.Clear();

                // Varsayılanları yeniden yükle;
                LoadDefaultDesignTokens();
                LoadDefaultColorPalettes();
                LoadDefaultTypographyScales();
                LoadDefaultComponentStyles();
                LoadDefaultDesignSystems();

                // Aktif tasarım sistemini yeniden ayarla;
                SetActiveDesignSystem(_configuration.DefaultDesignSystem);

                UpdateStatistics();

                _logger.LogInformation("Stil kılavuzu başarıyla temizlendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stil kılavuzu temizleme sırasında hata oluştu.");
                throw new StyleGuideException("Stil kılavuzu temizlenemedi.", ex);
            }
        }

        #endregion;

        #region Private Methods;

        private void AddDesignTokenInternal(DesignToken token)
        {
            _designTokens.Add(token.Id, token);
        }

        private void AddColorPaletteInternal(ColorPalette palette)
        {
            _colorPalettes.Add(palette.Id, palette);
        }

        private void AddTypographyScaleInternal(TypographyScale scale)
        {
            _typographyScales.Add(scale.Id, scale);
        }

        private void AddComponentStyleInternal(ComponentStyle style)
        {
            _componentStyles.Add(style.Id, style);
        }

        private void AddDesignSystemInternal(DesignSystem system)
        {
            _designSystems.Add(system.Id, system);
        }

        private async Task<Dictionary<string, string>> ResolveStylePropertiesAsync(ComponentStyle style, string variant, string state)
        {
            var resolvedProperties = new Dictionary<string, string>();

            // Temel özellikleri çözümle;
            foreach (var prop in style.Properties)
            {
                resolvedProperties[prop.Key] = ResolveToken(prop.Value);
            }

            // Varyant özelliklerini uygula;
            if (!string.IsNullOrEmpty(variant) && style.Variants != null)
            {
                if (style.Variants.TryGetValue(variant, out var variantProps))
                {
                    foreach (var prop in variantProps)
                    {
                        resolvedProperties[prop.Key] = ResolveToken(prop.Value);
                    }
                }
            }

            // Durum özelliklerini uygula;
            if (!string.IsNullOrEmpty(state) && style.States != null)
            {
                if (style.States.TryGetValue(state, out var stateProps))
                {
                    foreach (var prop in stateProps)
                    {
                        resolvedProperties[prop.Key] = ResolveToken(prop.Value);
                    }
                }
            }

            // Tasarım bağlamına göre özellikleri uyarla;
            if (_currentContext != null)
            {
                await ApplyContextAdjustmentsAsync(resolvedProperties);
            }

            return resolvedProperties;
        }

        private async Task ApplyPropertiesToElementAsync(object element, Dictionary<string, string> properties)
        {
            // Platforma özgü stil uygulama;
            // Bu kısım platforma göre implemente edilmelidir;
            // WPF, Unity, Web, vb.

            await Task.Run(() =>
            {
                // Burada reflection veya platforma özgü API'lar kullanılarak;
                // özellikler elemente uygulanır;

                // Örnek: WPF için;
                // if (element is FrameworkElement frameworkElement)
                // {
                //     foreach (var prop in properties)
                //     {
                //         frameworkElement.SetValue(prop.Key, prop.Value);
                //     }
                // }

                // Unity için;
                // if (element is GameObject gameObject)
                // {
                //     var image = gameObject.GetComponent<Image>();
                //     if (image != null && properties.TryGetValue("backgroundColor", out var color))
                //     {
                //         ColorUtility.TryParseHtmlString(color, out var unityColor);
                //         image.color = unityColor;
                //     }
                // }
            });
        }

        private async Task ApplyContextAdjustmentsAsync(Dictionary<string, string> properties)
        {
            // Tasarım bağlamına göre özellikleri uyarla;
            // Örneğin: Karanlık mod, yüksek kontrast, mobil, vb.

            await Task.Run(() =>
            {
                if (_currentContext == DesignContext.DarkMode)
                {
                    // Karanlık mod ayarlamaları;
                    if (properties.ContainsKey("backgroundColor"))
                    {
                        var bgColor = properties["backgroundColor"];
                        if (bgColor == "white" || bgColor == "#FFFFFF")
                        {
                            properties["backgroundColor"] = "#1A1A1A";
                        }
                    }

                    if (properties.ContainsKey("color"))
                    {
                        var textColor = properties["color"];
                        if (textColor == "#000000" || textColor == "black")
                        {
                            properties["color"] = "#FFFFFF";
                        }
                    }
                }
                else if (_currentContext == DesignContext.HighContrast)
                {
                    // Yüksek kontrast ayarlamaları;
                    if (properties.ContainsKey("backgroundColor"))
                    {
                        properties["backgroundColor"] = "#000000";
                    }

                    if (properties.ContainsKey("color"))
                    {
                        properties["color"] = "#FFFFFF";
                    }

                    if (properties.ContainsKey("border"))
                    {
                        properties["border"] = "2px solid #FFFF00";
                    }
                }
            });
        }

        private void UpdateStatistics()
        {
            Statistics = new StyleGuideStatistics;
            {
                DesignTokenCount = _designTokens.Count,
                ColorPaletteCount = _colorPalettes.Count,
                TypographyScaleCount = _typographyScales.Count,
                ComponentStyleCount = _componentStyles.Count,
                DesignSystemCount = _designSystems.Count,
                ActiveDesignSystem = _activeDesignSystem?.Name,
                LastUpdated = DateTime.UtcNow;
            };
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnStyleGuideInitialized(StyleGuideInitializedEventArgs e)
        {
            StyleGuideInitialized?.Invoke(this, e);
        }

        protected virtual void OnDesignSystemChanged(DesignSystemChangedEventArgs e)
        {
            DesignSystemChanged?.Invoke(this, e);
        }

        protected virtual void OnDesignContextChanged(DesignContextChangedEventArgs e)
        {
            DesignContextChanged?.Invoke(this, e);
        }

        protected virtual void OnDesignTokenAdded(DesignTokenEventArgs e)
        {
            DesignTokenAdded?.Invoke(this, e);
        }

        protected virtual void OnColorPaletteAdded(ColorPaletteEventArgs e)
        {
            ColorPaletteAdded?.Invoke(this, e);
        }

        protected virtual void OnComponentStyleAdded(ComponentStyleEventArgs e)
        {
            ComponentStyleAdded?.Invoke(this, e);
        }

        protected virtual void OnStyleApplied(StyleAppliedEventArgs e)
        {
            StyleApplied?.Invoke(this, e);
        }

        protected virtual void OnStyleValidated(StyleValidatedEventArgs e)
        {
            StyleValidated?.Invoke(this, e);
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
                    // Yönetilen kaynakları serbest bırak;
                    ClearStyleGuide();

                    // Alt sistemleri dispose et;
                    (_styleValidator as IDisposable)?.Dispose();
                    (_styleExporter as IDisposable)?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~StyleGuide()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface IStyleGuide : IDisposable
    {
        void AddDesignToken(DesignToken token);
        void AddColorPalette(ColorPalette palette);
        void AddComponentStyle(ComponentStyle style);
        void AddDesignSystem(DesignSystem system);
        Task ApplyStyleAsync(object element, string styleId, string variant = null, string state = null);
        string ResolveToken(string tokenReference);
        Task<ValidationResult> ValidateTokensAsync(string tokenId = null);
        ValidationResult ValidateColorPalette(string paletteId);
        AccessibilityResult CheckAccessibility(string foregroundColor, string backgroundColor);
        Task<StyleExportData> ExportStyleGuideAsync(StyleExportType exportType, ExportOptions options = null);
        Task ImportStyleGuideAsync(byte[] importData, StyleImportType importType);
        void ClearStyleGuide();

        DesignToken FindDesignToken(string tokenId);
        ColorPalette FindColorPalette(string paletteId);
        ComponentStyle FindComponentStyle(string styleId);
        IEnumerable<DesignToken> GetTokensByCategory(TokenCategory category);
        IEnumerable<ComponentStyle> GetStylesByComponentType(ComponentType componentType);

        IReadOnlyDictionary<string, DesignToken> DesignTokens { get; }
        IReadOnlyDictionary<string, ColorPalette> ColorPalettes { get; }
        IReadOnlyDictionary<string, ComponentStyle> ComponentStyles { get; }
        IReadOnlyDictionary<string, DesignSystem> DesignSystems { get; }
        DesignSystem ActiveDesignSystem { get; }
        DesignContext CurrentContext { get; }
        StyleGuideStatistics Statistics { get; }
        string Version { get; }

        event EventHandler<StyleGuideInitializedEventArgs> StyleGuideInitialized;
        event EventHandler<DesignSystemChangedEventArgs> DesignSystemChanged;
        event EventHandler<DesignContextChangedEventArgs> DesignContextChanged;
        event EventHandler<DesignTokenEventArgs> DesignTokenAdded;
        event EventHandler<ColorPaletteEventArgs> ColorPaletteAdded;
        event EventHandler<ComponentStyleEventArgs> ComponentStyleAdded;
        event EventHandler<StyleAppliedEventArgs> StyleApplied;
        event EventHandler<StyleValidatedEventArgs> StyleValidated;
    }

    public enum TokenCategory;
    {
        Color,
        Typography,
        Spacing,
        Border,
        Shadow,
        Animation,
        Layout;
    }

    public enum ComponentType;
    {
        Button,
        Input,
        Card,
        Modal,
        Alert,
        Navigation,
        Table,
        Form,
        Icon,
        Avatar,
        Badge,
        Progress,
        Tooltip,
        Dropdown,
        Tabs,
        Accordion,
        Breadcrumb,
        Pagination,
        Rating,
        Slider,
        Switch,
        Checkbox,
        Radio;
    }

    public enum StyleExportType;
    {
        JSON,
        CSS,
        XAML,
        UNITY,
        HTML,
        SCSS,
        LESS;
    }

    public enum StyleImportType;
    {
        JSON,
        CSS,
        XAML,
        DesignTokenJSON;
    }

    public class StyleGuideInitializedEventArgs : EventArgs;
    {
        public DateTime Timestamp { get; set; }
        public int DesignTokenCount { get; set; }
        public int ColorPaletteCount { get; set; }
        public int ComponentStyleCount { get; set; }
        public string Version { get; set; }
    }

    public class DesignSystemChangedEventArgs : EventArgs;
    {
        public DesignSystem PreviousSystem { get; }
        public DesignSystem NewSystem { get; }

        public DesignSystemChangedEventArgs(DesignSystem previousSystem, DesignSystem newSystem)
        {
            PreviousSystem = previousSystem;
            NewSystem = newSystem;
        }
    }

    public class DesignContextChangedEventArgs : EventArgs;
    {
        public DesignContext PreviousContext { get; }
        public DesignContext NewContext { get; }

        public DesignContextChangedEventArgs(DesignContext previousContext, DesignContext newContext)
        {
            PreviousContext = previousContext;
            NewContext = newContext;
        }
    }

    public class DesignTokenEventArgs : EventArgs;
    {
        public string TokenId { get; set; }
        public TokenCategory Category { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ColorPaletteEventArgs : EventArgs;
    {
        public string PaletteId { get; set; }
        public int ColorCount { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class ComponentStyleEventArgs : EventArgs;
    {
        public string StyleId { get; set; }
        public ComponentType ComponentType { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StyleAppliedEventArgs : EventArgs;
    {
        public string ElementType { get; set; }
        public string StyleId { get; set; }
        public string Variant { get; set; }
        public string State { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StyleValidatedEventArgs : EventArgs;
    {
        public string TokenId { get; set; }
        public string PaletteId { get; set; }
        public ValidationResult Result { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class StyleExportData;
    {
        public string ExportId { get; set; }
        public StyleExportType ExportType { get; set; }
        public byte[] Data { get; set; }
        public DateTime ExportTime { get; set; }
        public DesignSystem DesignSystem { get; set; }
        public int TokenCount { get; set; }
        public int PaletteCount { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class StyleGuideException : Exception
    {
        public StyleGuideException(string message) : base(message) { }
        public StyleGuideException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
