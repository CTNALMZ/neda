using NEDA.AI.ComputerVision;
using NEDA.Animation.SequenceEditor.CinematicDirector;
using NEDA.Core.Common.Utilities;
using NEDA.Core.ExceptionHandling;
using NEDA.Core.Logging;
using NEDA.GameDesign.UX_UI_Design.MenuSystems.Configuration;
using NEDA.GameDesign.UX_UI_Design.MenuSystems.DataModels;
using NEDA.GameDesign.UX_UI_Design.MenuSystems.Routing;
using NEDA.GameDesign.UX_UI_Design.MenuSystems.Transitions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Transactions;
using static NEDA.GameDesign.LevelDesign.NavigationMesh.AINavigation;

namespace NEDA.GameDesign.UX_UI_Design.MenuSystems;
{
    /// <summary>
    /// Menü sistemi gezinme yönetimi, yönlendirme, geçmiş yönetimi ve geçiş efektleri sağlar.
    /// Endüstriyel seviyede gezinme sistemi ve state management sağlar.
    /// </summary>
    public class Navigation : INavigation, IDisposable;
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly ITransitionManager _transitionManager;
        private readonly IRouteManager _routeManager;
        private readonly NavigationConfiguration _configuration;

        private readonly Stack<NavigationState> _backStack;
        private readonly Stack<NavigationState> _forwardStack;
        private readonly Dictionary<string, NavigationRoute> _routes;
        private readonly Dictionary<string, NavigationMiddleware> _middlewares;

        private NavigationState _currentState;
        private bool _isInitialized;
        private bool _isNavigating;
        private bool _isTransitioning;
        private readonly object _navigationLock = new object();

        /// <summary>
        /// Gezinme geçmişi (geri yığını)
        /// </summary>
        public IReadOnlyCollection<NavigationState> BackHistory => _backStack;

        /// <summary>
        /// İleri gezinme yığını;
        /// </summary>
        public IReadOnlyCollection<NavigationState> ForwardHistory => _forwardStack;

        /// <summary>
        /// Kayıtlı rotalar;
        /// </summary>
        public IReadOnlyDictionary<string, NavigationRoute> Routes => _routes;

        /// <summary>
        /// Kayıtlı middleware'lar;
        /// </summary>
        public IReadOnlyDictionary<string, NavigationMiddleware> Middlewares => _middlewares;

        /// <summary>
        /// Mevcut gezinme durumu;
        /// </summary>
        public NavigationState CurrentState;
        {
            get => _currentState;
            private set;
            {
                if (_currentState != value)
                {
                    var oldState = _currentState;
                    _currentState = value;
                    OnNavigationStateChanged(new NavigationStateChangedEventArgs(oldState, value));
                }
            }
        }

        /// <summary>
        /// Gezinme istatistikleri;
        /// </summary>
        public NavigationStatistics Statistics { get; private set; }

        /// <summary>
        /// Gezinme modu (normal, modal, wizard, vb.)
        /// </summary>
        public NavigationMode NavigationMode { get; private set; }

        /// <summary>
        /// Gezinme etkin mi;
        /// </summary>
        public bool IsEnabled { get; set; }

        #endregion;

        #region Events;

        /// <summary>
        /// Gezinme başlatıldığında tetiklenir;
        /// </summary>
        public event EventHandler<NavigationEventArgs> NavigationStarted;

        /// <summary>
        /// Gezinme tamamlandığında tetiklenir;
        /// </summary>
        public event EventHandler<NavigationEventArgs> NavigationCompleted;

        /// <summary>
        /// Gezinme iptal edildiğinde tetiklenir;
        /// </summary>
        public event EventHandler<NavigationEventArgs> NavigationCancelled;

        /// <summary>
        /// Gezinme durumu değiştiğinde tetiklenir;
        /// </summary>
        public event EventHandler<NavigationStateChangedEventArgs> NavigationStateChanged;

        /// <summary>
        /// Geri gezinme yapıldığında tetiklenir;
        /// </summary>
        public event EventHandler<NavigationEventArgs> NavigatedBack;

        /// <summary>
        /// İleri gezinme yapıldığında tetiklenir;
        /// </summary>
        public event EventHandler<NavigationEventArgs> NavigatedForward;

        /// <summary>
        /// Rota eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<RouteEventArgs> RouteAdded;

        /// <summary>
        /// Middleware eklendiğinde tetiklenir;
        /// </summary>
        public event EventHandler<MiddlewareEventArgs> MiddlewareAdded;

        #endregion;

        #region Constructor;

        /// <summary>
        /// Navigation sınıfının yeni bir örneğini oluşturur;
        /// </summary>
        /// <param name="logger">Loglama servisi</param>
        /// <param name="transitionManager">Geçiş efekti yöneticisi</param>
        /// <param name="routeManager">Rota yöneticisi</param>
        /// <param name="configuration">Gezinme konfigürasyonu</param>
        public Navigation(
            ILogger logger,
            ITransitionManager transitionManager,
            IRouteManager routeManager,
            NavigationConfiguration configuration)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transitionManager = transitionManager ?? throw new ArgumentNullException(nameof(transitionManager));
            _routeManager = routeManager ?? throw new ArgumentNullException(nameof(routeManager));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));

            _backStack = new Stack<NavigationState>();
            _forwardStack = new Stack<NavigationState>();
            _routes = new Dictionary<string, NavigationRoute>();
            _middlewares = new Dictionary<string, NavigationMiddleware>();

            Initialize();
        }

        #endregion;

        #region Initialization;

        private void Initialize()
        {
            try
            {
                _logger.LogInformation("Navigation başlatılıyor...");

                CurrentState = NavigationState.Initial;
                Statistics = new NavigationStatistics();
                NavigationMode = NavigationMode.Normal;
                IsEnabled = true;

                // Konfigürasyon validasyonu;
                ValidateConfiguration(_configuration);

                // Geçiş yöneticisini başlat;
                InitializeTransitionManager();

                // Rota yöneticisini başlat;
                InitializeRouteManager();

                // Varsayılan rotaları yükle;
                LoadDefaultRoutes();

                // Varsayılan middleware'ları yükle;
                LoadDefaultMiddlewares();

                _isInitialized = true;

                _logger.LogInformation("Navigation başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Navigation başlatma sırasında hata oluştu.");
                throw new NavigationException("Navigation başlatılamadı.", ex);
            }
        }

        private void ValidateConfiguration(NavigationConfiguration config)
        {
            if (config.MaxHistorySize <= 0)
                throw new ArgumentException("Maksimum geçmiş boyutu pozitif olmalıdır.");

            if (config.DefaultTransitionDuration <= 0)
                throw new ArgumentException("Varsayılan geçiş süresi pozitif olmalıdır.");

            if (string.IsNullOrEmpty(config.DefaultRoute))
                throw new ArgumentException("Varsayılan rota belirtilmelidir.");
        }

        private void InitializeTransitionManager()
        {
            _transitionManager.Initialize(_configuration.TransitionSettings);

            // Geçiş olaylarını dinle;
            _transitionManager.TransitionStarted += OnTransitionStarted;
            _transitionManager.TransitionCompleted += OnTransitionCompleted;
            _transitionManager.TransitionCancelled += OnTransitionCancelled;
        }

        private void InitializeRouteManager()
        {
            _routeManager.Initialize(_configuration.RouteSettings);

            // Rota olaylarını dinle;
            _routeManager.RouteResolved += OnRouteResolved;
            _routeManager.RouteNotFound += OnRouteNotFound;
        }

        private void LoadDefaultRoutes()
        {
            // Varsayılan rotalar;
            var defaultRoutes = new[]
            {
                new NavigationRoute;
                {
                    Id = "main-menu",
                    Name = "Main Menu",
                    Path = "/main",
                    ComponentType = "MainMenuView",
                    RequiresAuthentication = false,
                    Permissions = Array.Empty<string>(),
                    Metadata = new RouteMetadata;
                    {
                        Title = "Main Menu",
                        Icon = "home",
                        Description = "Ana menü sayfası",
                        IsVisibleInMenu = true,
                        MenuOrder = 1;
                    }
                },
                new NavigationRoute;
                {
                    Id = "settings",
                    Name = "Settings",
                    Path = "/settings",
                    ComponentType = "SettingsView",
                    RequiresAuthentication = true,
                    Permissions = new[] { "settings.view" },
                    Metadata = new RouteMetadata;
                    {
                        Title = "Settings",
                        Icon = "settings",
                        Description = "Ayarlar sayfası",
                        IsVisibleInMenu = true,
                        MenuOrder = 2;
                    }
                },
                new NavigationRoute;
                {
                    Id = "profile",
                    Name = "User Profile",
                    Path = "/profile",
                    ComponentType = "ProfileView",
                    RequiresAuthentication = true,
                    Permissions = new[] { "profile.view" },
                    Metadata = new RouteMetadata;
                    {
                        Title = "Profile",
                        Icon = "user",
                        Description = "Kullanıcı profili sayfası",
                        IsVisibleInMenu = true,
                        MenuOrder = 3;
                    }
                },
                new NavigationRoute;
                {
                    Id = "about",
                    Name = "About",
                    Path = "/about",
                    ComponentType = "AboutView",
                    RequiresAuthentication = false,
                    Permissions = Array.Empty<string>(),
                    Metadata = new RouteMetadata;
                    {
                        Title = "About",
                        Icon = "info",
                        Description = "Hakkında sayfası",
                        IsVisibleInMenu = true,
                        MenuOrder = 4;
                    }
                }
            };

            foreach (var route in defaultRoutes)
            {
                RegisterRouteInternal(route);
            }

            // Varsayılan rotayı ayarla;
            if (_routes.ContainsKey(_configuration.DefaultRoute))
            {
                var defaultRoute = _routes[_configuration.DefaultRoute];
                CurrentState = new NavigationState;
                {
                    Route = defaultRoute,
                    Parameters = new Dictionary<string, object>(),
                    Timestamp = DateTime.UtcNow;
                };
            }
        }

        private void LoadDefaultMiddlewares()
        {
            // Varsayılan middleware'lar;
            var defaultMiddlewares = new[]
            {
                new NavigationMiddleware;
                {
                    Id = "authentication",
                    Name = "Authentication Middleware",
                    Priority = 100,
                    Handler = async (context, next) =>
                    {
                        if (context.Route.RequiresAuthentication)
                        {
                            // Authentication kontrolü burada yapılır;
                            _logger.LogDebug($"Authentication kontrolü: {context.Route.Id}");
                            
                            // Örnek: Eğer kullanıcı giriş yapmamışsa, login sayfasına yönlendir;
                            // if (!IsUserAuthenticated())
                            // {
                            //     context.CancelNavigation = true;
                            //     context.RedirectTo = "/login";
                            //     return;
                            // }
                        }

                        await next();
                    }
                },
                new NavigationMiddleware;
                {
                    Id = "authorization",
                    Name = "Authorization Middleware",
                    Priority = 200,
                    Handler = async (context, next) =>
                    {
                        if (context.Route.Permissions != null && context.Route.Permissions.Any())
                        {
                            // Yetki kontrolü burada yapılır;
                            _logger.LogDebug($"Authorization kontrolü: {context.Route.Id}");
                            
                            // Örnek: Eğer kullanıcının gerekli yetkileri yoksa, erişim reddedilir;
                            // if (!HasPermissions(context.Route.Permissions))
                            // {
                            //     context.CancelNavigation = true;
                            //     context.RedirectTo = "/access-denied";
                            //     return;
                            // }
                        }

                        await next();
                    }
                },
                new NavigationMiddleware;
                {
                    Id = "logging",
                    Name = "Logging Middleware",
                    Priority = 300,
                    Handler = async (context, next) =>
                    {
                        var startTime = DateTime.UtcNow;
                        _logger.LogInformation($"Gezinme başlatıldı: {context.Route.Id}");

                        await next();

                        var duration = DateTime.UtcNow - startTime;
                        _logger.LogInformation($"Gezinme tamamlandı: {context.Route.Id} ({duration.TotalMilliseconds}ms)");
                    }
                },
                new NavigationMiddleware;
                {
                    Id = "analytics",
                    Name = "Analytics Middleware",
                    Priority = 400,
                    Handler = async (context, next) =>
                    {
                        // Analitik veri toplama;
                        _logger.LogDebug($"Analitik veri toplanıyor: {context.Route.Id}");

                        await next();
                        
                        // Gezinme tamamlandıktan sonra analitik veriyi gönder;
                        // SendAnalyticsData(context);
                    }
                }
            };

            foreach (var middleware in defaultMiddlewares)
            {
                RegisterMiddlewareInternal(middleware);
            }
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Belirtilen rotaya gezinir;
        /// </summary>
        /// <param name="routeId">Rota ID</param>
        /// <param name="parameters">Rota parametreleri</param>
        /// <param name="options">Gezinme seçenekleri</param>
        /// <returns>Gezinme sonucu</returns>
        public async Task<NavigationResult> NavigateAsync(string routeId, IDictionary<string, object> parameters = null, NavigationOptions options = null)
        {
            if (!IsEnabled)
                return NavigationResult.CreateFailed("Gezinme devre dışı bırakıldı.");

            if (!_isInitialized)
                return NavigationResult.CreateFailed("Navigation başlatılmamış.");

            if (_isNavigating)
                return NavigationResult.CreateFailed("Başka bir gezinme işlemi devam ediyor.");

            lock (_navigationLock)
            {
                if (_isNavigating)
                    return NavigationResult.CreateFailed("Başka bir gezinme işlemi devam ediyor.");

                _isNavigating = true;
            }

            try
            {
                _logger.LogInformation($"Gezinme başlatılıyor: {routeId}");

                // Rota bul;
                if (!_routes.TryGetValue(routeId, out var route))
                {
                    _logger.LogWarning($"Rota bulunamadı: {routeId}");
                    return NavigationResult.CreateFailed($"Rota bulunamadı: {routeId}");
                }

                // Gezinme bağlamı oluştur;
                var navigationContext = new NavigationContext;
                {
                    From = CurrentState?.Route,
                    To = route,
                    Parameters = parameters ?? new Dictionary<string, object>(),
                    Options = options ?? new NavigationOptions(),
                    Timestamp = DateTime.UtcNow;
                };

                // Gezinme olayını tetikle;
                OnNavigationStarted(new NavigationEventArgs;
                {
                    Context = navigationContext,
                    Timestamp = DateTime.UtcNow;
                });

                // Middleware'ları çalıştır;
                var middlewareResult = await ExecuteMiddlewaresAsync(navigationContext);
                if (middlewareResult.Cancelled)
                {
                    _logger.LogInformation($"Gezinme middleware tarafından iptal edildi: {routeId}");

                    if (!string.IsNullOrEmpty(middlewareResult.RedirectTo))
                    {
                        // Yönlendirme yap;
                        return await NavigateAsync(middlewareResult.RedirectTo, parameters, options);
                    }

                    OnNavigationCancelled(new NavigationEventArgs;
                    {
                        Context = navigationContext,
                        Timestamp = DateTime.UtcNow,
                        Reason = middlewareResult.CancellationReason;
                    });

                    return NavigationResult.CreateCancelled(middlewareResult.CancellationReason);
                }

                // Geçiş efekti başlat (eğer varsa)
                if (navigationContext.Options.UseTransition && _transitionManager != null)
                {
                    lock (_navigationLock)
                    {
                        _isTransitioning = true;
                    }

                    var transitionResult = await _transitionManager.ExecuteTransitionAsync(
                        CurrentState,
                        navigationContext,
                        navigationContext.Options.TransitionType);

                    if (!transitionResult.Success)
                    {
                        _logger.LogWarning($"Geçiş efekti başarısız: {transitionResult.ErrorMessage}");
                    }

                    lock (_navigationLock)
                    {
                        _isTransitioning = false;
                    }
                }

                // Mevcut durumu geri yığınına ekle (eğer kaydetme seçeneği açıksa)
                if (navigationContext.Options.AddToBackStack && CurrentState != null)
                {
                    PushToBackStack(CurrentState);

                    // İleri yığınını temizle (yeni gezinme yapıldığında)
                    ClearForwardStack();
                }

                // Yeni gezinme durumu oluştur;
                var newState = new NavigationState;
                {
                    Route = route,
                    Parameters = navigationContext.Parameters,
                    NavigationMode = NavigationMode.Normal,
                    Timestamp = DateTime.UtcNow;
                };

                // Gezinme durumunu güncelle;
                CurrentState = newState;

                // İstatistikleri güncelle;
                UpdateStatistics(navigationContext);

                // Gezinme tamamlandı olayını tetikle;
                OnNavigationCompleted(new NavigationEventArgs;
                {
                    Context = navigationContext,
                    Timestamp = DateTime.UtcNow,
                    NavigationState = newState;
                });

                _logger.LogInformation($"Gezinme tamamlandı: {routeId}");

                return NavigationResult.CreateSuccess(newState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Gezinme sırasında hata oluştu: {routeId}");
                return NavigationResult.CreateFailed($"Gezinme hatası: {ex.Message}");
            }
            finally
            {
                lock (_navigationLock)
                {
                    _isNavigating = false;
                }
            }
        }

        /// <summary>
        /// Geri gezinir (back stack'ten)
        /// </summary>
        /// <param name="options">Gezinme seçenekleri</param>
        /// <returns>Gezinme sonucu</returns>
        public async Task<NavigationResult> GoBackAsync(NavigationOptions options = null)
        {
            if (!IsEnabled)
                return NavigationResult.CreateFailed("Gezinme devre dışı bırakıldı.");

            if (_backStack.Count == 0)
                return NavigationResult.CreateFailed("Geri gezinme geçmişi yok.");

            if (_isNavigating)
                return NavigationResult.CreateFailed("Başka bir gezinme işlemi devam ediyor.");

            lock (_navigationLock)
            {
                if (_isNavigating)
                    return NavigationResult.CreateFailed("Başka bir gezinme işlemi devam ediyor.");

                _isNavigating = true;
            }

            try
            {
                _logger.LogInformation("Geri gezinme başlatılıyor...");

                // Geri yığınından bir önceki durumu al;
                var previousState = _backStack.Pop();

                // Mevcut durumu ileri yığınına ekle;
                PushToForwardStack(CurrentState);

                // Gezinme bağlamı oluştur;
                var navigationContext = new NavigationContext;
                {
                    From = CurrentState?.Route,
                    To = previousState.Route,
                    Parameters = previousState.Parameters,
                    Options = options ?? new NavigationOptions(),
                    Timestamp = DateTime.UtcNow,
                    NavigationDirection = NavigationDirection.Back;
                };

                // Geri gezinme olayını tetikle;
                OnNavigationStarted(new NavigationEventArgs;
                {
                    Context = navigationContext,
                    Timestamp = DateTime.UtcNow;
                });

                // Middleware'ları çalıştır;
                var middlewareResult = await ExecuteMiddlewaresAsync(navigationContext);
                if (middlewareResult.Cancelled)
                {
                    _logger.LogInformation($"Geri gezinme middleware tarafından iptal edildi");

                    // İptal edildi, durumu geri yığınına geri ekle;
                    _backStack.Push(previousState);
                    _forwardStack.Pop(); // Mevcut durumu ileri yığınından çıkar;

                    return NavigationResult.CreateCancelled(middlewareResult.CancellationReason);
                }

                // Geçiş efekti başlat (eğer varsa)
                if (navigationContext.Options.UseTransition && _transitionManager != null)
                {
                    lock (_navigationLock)
                    {
                        _isTransitioning = true;
                    }

                    var transitionResult = await _transitionManager.ExecuteTransitionAsync(
                        CurrentState,
                        navigationContext,
                        navigationContext.Options.TransitionType ?? TransitionType.SlideRight);

                    if (!transitionResult.Success)
                    {
                        _logger.LogWarning($"Geçiş efekti başarısız: {transitionResult.ErrorMessage}");
                    }

                    lock (_navigationLock)
                    {
                        _isTransitioning = false;
                    }
                }

                // Gezinme durumunu güncelle;
                CurrentState = previousState;

                // İstatistikleri güncelle;
                UpdateStatistics(navigationContext);

                // Geri gezinme tamamlandı olayını tetikle;
                OnNavigatedBack(new NavigationEventArgs;
                {
                    Context = navigationContext,
                    Timestamp = DateTime.UtcNow,
                    NavigationState = previousState;
                });

                _logger.LogInformation("Geri gezinme tamamlandı.");

                return NavigationResult.CreateSuccess(previousState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Geri gezinme sırasında hata oluştu.");
                return NavigationResult.CreateFailed($"Geri gezinme hatası: {ex.Message}");
            }
            finally
            {
                lock (_navigationLock)
                {
                    _isNavigating = false;
                }
            }
        }

        /// <summary>
        /// İleri gezinir (forward stack'ten)
        /// </summary>
        /// <param name="options">Gezinme seçenekleri</param>
        /// <returns>Gezinme sonucu</returns>
        public async Task<NavigationResult> GoForwardAsync(NavigationOptions options = null)
        {
            if (!IsEnabled)
                return NavigationResult.CreateFailed("Gezinme devre dışı bırakıldı.");

            if (_forwardStack.Count == 0)
                return NavigationResult.CreateFailed("İleri gezinme geçmişi yok.");

            if (_isNavigating)
                return NavigationResult.CreateFailed("Başka bir gezinme işlemi devam ediyor.");

            lock (_navigationLock)
            {
                if (_isNavigating)
                    return NavigationResult.CreateFailed("Başka bir gezinme işlemi devam ediyor.");

                _isNavigating = true;
            }

            try
            {
                _logger.LogInformation("İleri gezinme başlatılıyor...");

                // İleri yığınından bir sonraki durumu al;
                var nextState = _forwardStack.Pop();

                // Mevcut durumu geri yığınına ekle;
                PushToBackStack(CurrentState);

                // Gezinme bağlamı oluştur;
                var navigationContext = new NavigationContext;
                {
                    From = CurrentState?.Route,
                    To = nextState.Route,
                    Parameters = nextState.Parameters,
                    Options = options ?? new NavigationOptions(),
                    Timestamp = DateTime.UtcNow,
                    NavigationDirection = NavigationDirection.Forward;
                };

                // İleri gezinme olayını tetikle;
                OnNavigationStarted(new NavigationEventArgs;
                {
                    Context = navigationContext,
                    Timestamp = DateTime.UtcNow;
                });

                // Middleware'ları çalıştır;
                var middlewareResult = await ExecuteMiddlewaresAsync(navigationContext);
                if (middlewareResult.Cancelled)
                {
                    _logger.LogInformation($"İleri gezinme middleware tarafından iptal edildi");

                    // İptal edildi, durumu ileri yığınına geri ekle;
                    _forwardStack.Push(nextState);
                    _backStack.Pop(); // Mevcut durumu geri yığınından çıkar;

                    return NavigationResult.CreateCancelled(middlewareResult.CancellationReason);
                }

                // Geçiş efekti başlat (eğer varsa)
                if (navigationContext.Options.UseTransition && _transitionManager != null)
                {
                    lock (_navigationLock)
                    {
                        _isTransitioning = true;
                    }

                    var transitionResult = await _transitionManager.ExecuteTransitionAsync(
                        CurrentState,
                        navigationContext,
                        navigationContext.Options.TransitionType ?? TransitionType.SlideLeft);

                    if (!transitionResult.Success)
                    {
                        _logger.LogWarning($"Geçiş efekti başarısız: {transitionResult.ErrorMessage}");
                    }

                    lock (_navigationLock)
                    {
                        _isTransitioning = false;
                    }
                }

                // Gezinme durumunu güncelle;
                CurrentState = nextState;

                // İstatistikleri güncelle;
                UpdateStatistics(navigationContext);

                // İleri gezinme tamamlandı olayını tetikle;
                OnNavigatedForward(new NavigationEventArgs;
                {
                    Context = navigationContext,
                    Timestamp = DateTime.UtcNow,
                    NavigationState = nextState;
                });

                _logger.LogInformation("İleri gezinme tamamlandı.");

                return NavigationResult.CreateSuccess(nextState);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "İleri gezinme sırasında hata oluştu.");
                return NavigationResult.CreateFailed($"İleri gezinme hatası: {ex.Message}");
            }
            finally
            {
                lock (_navigationLock)
                {
                    _isNavigating = false;
                }
            }
        }

        /// <summary>
        /// Rota kaydeder;
        /// </summary>
        /// <param name="route">Rota</param>
        public void RegisterRoute(NavigationRoute route)
        {
            if (route == null)
                throw new ArgumentNullException(nameof(route));

            if (_routes.ContainsKey(route.Id))
                throw new NavigationException($"Rota zaten kayıtlı: {route.Id}");

            try
            {
                RegisterRouteInternal(route);

                OnRouteAdded(new RouteEventArgs;
                {
                    RouteId = route.Id,
                    RouteName = route.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Rota kaydedildi: {route.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Rota kaydetme sırasında hata oluştu: {route.Id}");
                throw new NavigationException($"Rota kaydedilemedi: {route.Id}", ex);
            }
        }

        /// <summary>
        /// Middleware kaydeder;
        /// </summary>
        /// <param name="middleware">Middleware</param>
        public void RegisterMiddleware(NavigationMiddleware middleware)
        {
            if (middleware == null)
                throw new ArgumentNullException(nameof(middleware));

            if (_middlewares.ContainsKey(middleware.Id))
                throw new NavigationException($"Middleware zaten kayıtlı: {middleware.Id}");

            try
            {
                RegisterMiddlewareInternal(middleware);

                OnMiddlewareAdded(new MiddlewareEventArgs;
                {
                    MiddlewareId = middleware.Id,
                    MiddlewareName = middleware.Name,
                    Timestamp = DateTime.UtcNow;
                });

                _logger.LogInformation($"Middleware kaydedildi: {middleware.Id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Middleware kaydetme sırasında hata oluştu: {middleware.Id}");
                throw new NavigationException($"Middleware kaydedilemedi: {middleware.Id}", ex);
            }
        }

        /// <summary>
        /// Gezinme geçmişini temizler;
        /// </summary>
        /// <param name="clearBackStack">Geri yığınını temizle</param>
        /// <param name="clearForwardStack">İleri yığınını temizle</param>
        public void ClearHistory(bool clearBackStack = true, bool clearForwardStack = true)
        {
            try
            {
                _logger.LogInformation("Gezinme geçmişi temizleniyor...");

                if (clearBackStack)
                {
                    _backStack.Clear();
                    _logger.LogDebug("Geri yığını temizlendi.");
                }

                if (clearForwardStack)
                {
                    _forwardStack.Clear();
                    _logger.LogDebug("İleri yığını temizlendi.");
                }

                _logger.LogInformation("Gezinme geçmişi temizlendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gezinme geçmişi temizleme sırasında hata oluştu.");
                throw new NavigationException("Gezinme geçmişi temizlenemedi.", ex);
            }
        }

        /// <summary>
        /// Gezinme modunu değiştirir;
        /// </summary>
        /// <param name="mode">Yeni gezinme modu</param>
        public void SetNavigationMode(NavigationMode mode)
        {
            NavigationMode = mode;
            _logger.LogDebug($"Gezinme modu değiştirildi: {mode}");
        }

        /// <summary>
        /// URL veya path'e göre gezinir;
        /// </summary>
        /// <param name="path">URL veya path</param>
        /// <param name="parameters">Parametreler</param>
        /// <param name="options">Gezinme seçenekleri</param>
        /// <returns>Gezinme sonucu</returns>
        public async Task<NavigationResult> NavigateToPathAsync(string path, IDictionary<string, object> parameters = null, NavigationOptions options = null)
        {
            if (string.IsNullOrEmpty(path))
                return NavigationResult.CreateFailed("Path boş olamaz.");

            try
            {
                // Rota yöneticisi kullanarak path'i çöz;
                var routeResolution = await _routeManager.ResolveRouteAsync(path);

                if (!routeResolution.Success)
                {
                    return NavigationResult.CreateFailed($"Rota çözümlenemedi: {path}");
                }

                // Parametreleri birleştir;
                var mergedParameters = new Dictionary<string, object>();
                if (parameters != null)
                {
                    foreach (var param in parameters)
                    {
                        mergedParameters[param.Key] = param.Value;
                    }
                }

                if (routeResolution.Parameters != null)
                {
                    foreach (var param in routeResolution.Parameters)
                    {
                        mergedParameters[param.Key] = param.Value;
                    }
                }

                // Gezinme yap;
                return await NavigateAsync(routeResolution.Route.Id, mergedParameters, options);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Path gezinme sırasında hata oluştu: {path}");
                return NavigationResult.CreateFailed($"Path gezinme hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Modal açma işlemi yapar;
        /// </summary>
        /// <param name="routeId">Modal rotası ID</param>
        /// <param name="parameters">Parametreler</param>
        /// <param name="modalOptions">Modal seçenekleri</param>
        /// <returns>Gezinme sonucu</returns>
        public async Task<NavigationResult> OpenModalAsync(string routeId, IDictionary<string, object> parameters = null, ModalOptions modalOptions = null)
        {
            if (!_routes.ContainsKey(routeId))
                return NavigationResult.CreateFailed($"Rota bulunamadı: {routeId}");

            try
            {
                // Modal için gezinme seçenekleri;
                var options = new NavigationOptions;
                {
                    UseTransition = true,
                    AddToBackStack = false, // Modal'lar genelde back stack'e eklenmez;
                    TransitionType = TransitionType.Fade;
                };

                // Modal açma;
                var result = await NavigateAsync(routeId, parameters, options);

                if (result.Success)
                {
                    // Modal moduna geç;
                    SetNavigationMode(NavigationMode.Modal);
                    _logger.LogDebug($"Modal açıldı: {routeId}");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Modal açma sırasında hata oluştu: {routeId}");
                return NavigationResult.CreateFailed($"Modal açma hatası: {ex.Message}");
            }
        }

        /// <summary>
        /// Modal kapatma işlemi yapar;
        /// </summary>
        /// <param name="result">Modal sonucu</param>
        /// <returns>Kapatma sonucu</returns>
        public async Task<bool> CloseModalAsync(object result = null)
        {
            try
            {
                _logger.LogDebug("Modal kapatılıyor...");

                // Normal moda geç;
                SetNavigationMode(NavigationMode.Normal);

                // Modal kapandığında geri gezinme yapılabilir;
                // veya özel bir işlem yapılabilir;

                _logger.LogInformation("Modal kapatıldı.");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Modal kapatma sırasında hata oluştu.");
                return false;
            }
        }

        /// <summary>
        /// Gezinme durumunu serileştirir;
        /// </summary>
        /// <returns>Serileştirilmiş durum</returns>
        public NavigationSerialization SerializeState()
        {
            try
            {
                var serialization = new NavigationSerialization;
                {
                    CurrentState = CurrentState,
                    BackStack = _backStack.ToList(),
                    ForwardStack = _forwardStack.ToList(),
                    Routes = _routes.Values.ToList(),
                    Middlewares = _middlewares.Values.ToList(),
                    Timestamp = DateTime.UtcNow,
                    Version = "1.0"
                };

                _logger.LogDebug("Gezinme durumu serileştirildi.");

                return serialization;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gezinme durumu serileştirme sırasında hata oluştu.");
                throw new NavigationException("Gezinme durumu serileştirilemedi.", ex);
            }
        }

        /// <summary>
        /// Serileştirilmiş durumu yükler;
        /// </summary>
        /// <param name="serialization">Serileştirilmiş durum</param>
        public void DeserializeState(NavigationSerialization serialization)
        {
            if (serialization == null)
                throw new ArgumentNullException(nameof(serialization));

            try
            {
                _logger.LogInformation("Gezinme durumu yükleniyor...");

                // Mevcut durumları temizle;
                ClearHistory(true, true);
                _routes.Clear();
                _middlewares.Clear();

                // Rotaları yükle;
                foreach (var route in serialization.Routes)
                {
                    RegisterRouteInternal(route);
                }

                // Middleware'ları yükle;
                foreach (var middleware in serialization.Middlewares)
                {
                    RegisterMiddlewareInternal(middleware);
                }

                // Yığınları yükle;
                foreach (var state in serialization.BackStack)
                {
                    _backStack.Push(state);
                }

                foreach (var state in serialization.ForwardStack)
                {
                    _forwardStack.Push(state);
                }

                // Mevcut durumu ayarla;
                if (serialization.CurrentState != null)
                {
                    CurrentState = serialization.CurrentState;
                }

                _logger.LogInformation("Gezinme durumu yüklendi.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gezinme durumu yükleme sırasında hata oluştu.");
                throw new NavigationException("Gezinme durumu yüklenemedi.", ex);
            }
        }

        #endregion;

        #region Private Methods;

        private void RegisterRouteInternal(NavigationRoute route)
        {
            _routes.Add(route.Id, route);

            // Rota yöneticisine de ekle;
            _routeManager.RegisterRoute(route);
        }

        private void RegisterMiddlewareInternal(NavigationMiddleware middleware)
        {
            _middlewares.Add(middleware.Id, middleware);
        }

        private async Task<MiddlewareExecutionResult> ExecuteMiddlewaresAsync(NavigationContext context)
        {
            var result = new MiddlewareExecutionResult();

            try
            {
                // Middleware'ları öncelik sırasına göre sırala;
                var sortedMiddlewares = _middlewares.Values;
                    .OrderBy(m => m.Priority)
                    .ToList();

                // Middleware zinciri oluştur;
                Func<Task> pipeline = () => Task.CompletedTask;

                foreach (var middleware in sortedMiddlewares.Reverse())
                {
                    var currentMiddleware = middleware;
                    var next = pipeline;

                    pipeline = () => currentMiddleware.Handler(context, next);
                }

                // Pipeline'ı çalıştır;
                await pipeline();

                // Middleware sonucunu döndür;
                result.Cancelled = context.CancelNavigation;
                result.RedirectTo = context.RedirectTo;
                result.CancellationReason = context.CancellationReason;

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Middleware çalıştırma sırasında hata oluştu.");
                result.Cancelled = true;
                result.CancellationReason = $"Middleware hatası: {ex.Message}";
                return result;
            }
        }

        private void PushToBackStack(NavigationState state)
        {
            if (state == null)
                return;

            // Maksimum geçmiş boyutunu kontrol et;
            if (_backStack.Count >= _configuration.MaxHistorySize)
            {
                // En eski girişi kaldır;
                var tempStack = new Stack<NavigationState>();

                // Son max-1 elemanı geçici yığına al;
                while (_backStack.Count > _configuration.MaxHistorySize - 1)
                {
                    tempStack.Push(_backStack.Pop());
                }

                // En eski elemanı at;
                if (tempStack.Count > 0)
                {
                    tempStack.Pop();
                }

                // Elemanları geri yığına geri ekle;
                while (tempStack.Count > 0)
                {
                    _backStack.Push(tempStack.Pop());
                }
            }

            _backStack.Push(state);
        }

        private void PushToForwardStack(NavigationState state)
        {
            if (state == null)
                return;

            // Maksimum geçmiş boyutunu kontrol et;
            if (_forwardStack.Count >= _configuration.MaxHistorySize)
            {
                // En eski girişi kaldır;
                var tempStack = new Stack<NavigationState>();

                while (_forwardStack.Count > _configuration.MaxHistorySize - 1)
                {
                    tempStack.Push(_forwardStack.Pop());
                }

                if (tempStack.Count > 0)
                {
                    tempStack.Pop();
                }

                while (tempStack.Count > 0)
                {
                    _forwardStack.Push(tempStack.Pop());
                }
            }

            _forwardStack.Push(state);
        }

        private void ClearForwardStack()
        {
            _forwardStack.Clear();
        }

        private void UpdateStatistics(NavigationContext context)
        {
            Statistics = new NavigationStatistics;
            {
                TotalNavigations = Statistics.TotalNavigations + 1,
                BackStackSize = _backStack.Count,
                ForwardStackSize = _forwardStack.Count,
                RouteCount = _routes.Count,
                MiddlewareCount = _middlewares.Count,
                LastNavigationTime = DateTime.UtcNow,
                LastRoute = context.To?.Id;
            };
        }

        private void OnTransitionStarted(object sender, TransitionEventArgs e)
        {
            _logger.LogDebug($"Geçiş başladı: {e.TransitionType}");
        }

        private void OnTransitionCompleted(object sender, TransitionEventArgs e)
        {
            _logger.LogDebug($"Geçiş tamamlandı: {e.TransitionType}");
        }

        private void OnTransitionCancelled(object sender, TransitionEventArgs e)
        {
            _logger.LogDebug($"Geçiş iptal edildi: {e.TransitionType}");
        }

        private void OnRouteResolved(object sender, RouteResolutionEventArgs e)
        {
            _logger.LogDebug($"Rota çözümlendi: {e.Route.Id}");
        }

        private void OnRouteNotFound(object sender, RouteResolutionEventArgs e)
        {
            _logger.LogWarning($"Rota bulunamadı: {e.Path}");
        }

        #endregion;

        #region Event Handlers;

        protected virtual void OnNavigationStarted(NavigationEventArgs e)
        {
            NavigationStarted?.Invoke(this, e);
        }

        protected virtual void OnNavigationCompleted(NavigationEventArgs e)
        {
            NavigationCompleted?.Invoke(this, e);
        }

        protected virtual void OnNavigationCancelled(NavigationEventArgs e)
        {
            NavigationCancelled?.Invoke(this, e);
        }

        protected virtual void OnNavigationStateChanged(NavigationStateChangedEventArgs e)
        {
            NavigationStateChanged?.Invoke(this, e);
        }

        protected virtual void OnNavigatedBack(NavigationEventArgs e)
        {
            NavigatedBack?.Invoke(this, e);
        }

        protected virtual void OnNavigatedForward(NavigationEventArgs e)
        {
            NavigatedForward?.Invoke(this, e);
        }

        protected virtual void OnRouteAdded(RouteEventArgs e)
        {
            RouteAdded?.Invoke(this, e);
        }

        protected virtual void OnMiddlewareAdded(MiddlewareEventArgs e)
        {
            MiddlewareAdded?.Invoke(this, e);
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
                    ClearHistory();

                    // Olay bağlantılarını kopar;
                    if (_transitionManager != null)
                    {
                        _transitionManager.TransitionStarted -= OnTransitionStarted;
                        _transitionManager.TransitionCompleted -= OnTransitionCompleted;
                        _transitionManager.TransitionCancelled -= OnTransitionCancelled;
                    }

                    if (_routeManager != null)
                    {
                        _routeManager.RouteResolved -= OnRouteResolved;
                        _routeManager.RouteNotFound -= OnRouteNotFound;
                    }

                    // Alt sistemleri dispose et;
                    (_transitionManager as IDisposable)?.Dispose();
                    (_routeManager as IDisposable)?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        ~Navigation()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Types and Interfaces;

    public interface INavigation : IDisposable
    {
        Task<NavigationResult> NavigateAsync(string routeId, IDictionary<string, object> parameters = null, NavigationOptions options = null);
        Task<NavigationResult> GoBackAsync(NavigationOptions options = null);
        Task<NavigationResult> GoForwardAsync(NavigationOptions options = null);
        Task<NavigationResult> NavigateToPathAsync(string path, IDictionary<string, object> parameters = null, NavigationOptions options = null);
        Task<NavigationResult> OpenModalAsync(string routeId, IDictionary<string, object> parameters = null, ModalOptions modalOptions = null);
        Task<bool> CloseModalAsync(object result = null);
        NavigationSerialization SerializeState();
        void DeserializeState(NavigationSerialization serialization);

        void RegisterRoute(NavigationRoute route);
        void RegisterMiddleware(NavigationMiddleware middleware);
        void ClearHistory(bool clearBackStack = true, bool clearForwardStack = true);
        void SetNavigationMode(NavigationMode mode);

        IReadOnlyCollection<NavigationState> BackHistory { get; }
        IReadOnlyCollection<NavigationState> ForwardHistory { get; }
        IReadOnlyDictionary<string, NavigationRoute> Routes { get; }
        IReadOnlyDictionary<string, NavigationMiddleware> Middlewares { get; }
        NavigationState CurrentState { get; }
        NavigationStatistics Statistics { get; }
        NavigationMode NavigationMode { get; }
        bool IsEnabled { get; set; }

        event EventHandler<NavigationEventArgs> NavigationStarted;
        event EventHandler<NavigationEventArgs> NavigationCompleted;
        event EventHandler<NavigationEventArgs> NavigationCancelled;
        event EventHandler<NavigationStateChangedEventArgs> NavigationStateChanged;
        event EventHandler<NavigationEventArgs> NavigatedBack;
        event EventHandler<NavigationEventArgs> NavigatedForward;
        event EventHandler<RouteEventArgs> RouteAdded;
        event EventHandler<MiddlewareEventArgs> MiddlewareAdded;
    }

    public enum NavigationMode;
    {
        Normal,
        Modal,
        Wizard,
        Tabbed,
        MasterDetail,
        Drawer;
    }

    public enum NavigationDirection;
    {
        Forward,
        Back,
        Replace,
        Refresh;
    }

    public class NavigationEventArgs : EventArgs;
    {
        public NavigationContext Context { get; set; }
        public NavigationState NavigationState { get; set; }
        public DateTime Timestamp { get; set; }
        public string Reason { get; set; }
    }

    public class NavigationStateChangedEventArgs : EventArgs;
    {
        public NavigationState PreviousState { get; }
        public NavigationState NewState { get; }

        public NavigationStateChangedEventArgs(NavigationState previousState, NavigationState newState)
        {
            PreviousState = previousState;
            NewState = newState;
        }
    }

    public class RouteEventArgs : EventArgs;
    {
        public string RouteId { get; set; }
        public string RouteName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class MiddlewareEventArgs : EventArgs;
    {
        public string MiddlewareId { get; set; }
        public string MiddlewareName { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class NavigationResult;
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public NavigationState State { get; set; }
        public string ErrorMessage { get; set; }
        public string CancellationReason { get; set; }
        public DateTime Timestamp { get; set; }

        public static NavigationResult CreateSuccess(NavigationState state)
        {
            return new NavigationResult;
            {
                Success = true,
                State = state,
                Timestamp = DateTime.UtcNow;
            };
        }

        public static NavigationResult CreateFailed(string errorMessage)
        {
            return new NavigationResult;
            {
                Success = false,
                ErrorMessage = errorMessage,
                Timestamp = DateTime.UtcNow;
            };
        }

        public static NavigationResult CreateCancelled(string reason = null)
        {
            return new NavigationResult;
            {
                Success = false,
                Cancelled = true,
                CancellationReason = reason,
                Timestamp = DateTime.UtcNow;
            };
        }
    }

    public class MiddlewareExecutionResult;
    {
        public bool Cancelled { get; set; }
        public string RedirectTo { get; set; }
        public string CancellationReason { get; set; }
    }

    public class NavigationException : Exception
    {
        public NavigationException(string message) : base(message) { }
        public NavigationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
