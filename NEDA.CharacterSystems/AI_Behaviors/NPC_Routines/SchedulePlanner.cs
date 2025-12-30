using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NEDA.Core.Common;
using NEDA.Core.Logging;
using NEDA.CharacterSystems.AI_Behaviors.BlackboardSystems;
using NEDA.CharacterSystems.AI_Behaviors.BehaviorTrees;

namespace NEDA.CharacterSystems.AI_Behaviors.NPC_Routines;
{
    /// <summary>
    /// NPC rutin planlama ve yönetim sistemi;
    /// Karakterlerin günlük aktivitelerini, zamanlamalarını ve davranış döngülerini yönetir;
    /// </summary>
    public class SchedulePlanner : IDisposable
    {
        #region Fields and Properties;

        private readonly ILogger _logger;
        private readonly Blackboard _blackboard;
        private readonly BehaviorTree _behaviorTree;
        private readonly Random _random;

        private Dictionary<string, NPCProfile> _npcProfiles;
        private Dictionary<string, DailySchedule> _activeSchedules;
        private Dictionary<string, List<ActivityTemplate>> _activityTemplates;
        private ScheduleConfiguration _configuration;

        private bool _isInitialized;
        private bool _isRunning;
        private DateTime _currentGameTime;
        private Task _schedulerTask;
        private readonly object _lockObject = new object();

        /// <summary>
        /// Planlamanın aktif olup olmadığını gösterir;
        /// </summary>
        public bool IsActive => _isRunning;

        /// <summary>
        /// Toplam yönetilen NPC sayısı;
        /// </summary>
        public int ManagedNpcCount => _npcProfiles?.Count ?? 0;

        /// <summary>
        /// Mevcut oyun saati;
        /// </summary>
        public DateTime CurrentGameTime;
        {
            get { lock (_lockObject) return _currentGameTime; }
            set { lock (_lockObject) _currentGameTime = value; }
        }

        #endregion;

        #region Constructors;

        /// <summary>
        /// SchedulePlanner yapıcı metodu;
        /// </summary>
        /// <param name="logger">Loglama sistemi</param>
        /// <param name="blackboard">AI karar destek sistemi</param>
        /// <param name="behaviorTree">Davranış ağacı</param>
        public SchedulePlanner(ILogger logger, Blackboard blackboard, BehaviorTree behaviorTree)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _blackboard = blackboard ?? throw new ArgumentNullException(nameof(blackboard));
            _behaviorTree = behaviorTree ?? throw new ArgumentNullException(nameof(behaviorTree));

            _random = new Random();
            _npcProfiles = new Dictionary<string, NPCProfile>();
            _activeSchedules = new Dictionary<string, DailySchedule>();
            _activityTemplates = new Dictionary<string, List<ActivityTemplate>>();

            _configuration = new ScheduleConfiguration();
            _isInitialized = false;
            _isRunning = false;
            _currentGameTime = DateTime.Today.AddHours(6); // Varsayılan: Sabah 6:00;
        }

        #endregion;

        #region Public Methods;

        /// <summary>
        /// Planlayıcıyı başlatır ve yapılandırır;
        /// </summary>
        /// <param name="configuration">Planlama yapılandırması</param>
        public async Task InitializeAsync(ScheduleConfiguration configuration = null)
        {
            if (_isInitialized)
            {
                _logger.LogWarning("SchedulePlanner zaten başlatılmış.");
                return;
            }

            try
            {
                _logger.LogInformation("SchedulePlanner başlatılıyor...");

                // Yapılandırmayı uygula;
                if (configuration != null)
                {
                    _configuration = configuration;
                }

                // Aktivite şablonlarını yükle;
                await LoadActivityTemplatesAsync();

                // Blackboard'a planlama değişkenlerini ekle;
                InitializeBlackboardVariables();

                // Davranış ağacı düğümlerini yapılandır;
                ConfigureBehaviorTree();

                _isInitialized = true;
                _logger.LogInformation("SchedulePlanner başarıyla başlatıldı.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"SchedulePlanner başlatma hatası: {ex.Message}", ex);
                throw new SchedulePlannerException("Planlayıcı başlatma hatası", ex);
            }
        }

        /// <summary>
        /// Yeni bir NPC profili ekler;
        /// </summary>
        /// <param name="npcId">NPC benzersiz kimliği</param>
        /// <param name="profile">NPC profili</param>
        public void AddNpcProfile(string npcId, NPCProfile profile)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                throw new ArgumentException("NPC ID boş olamaz.", nameof(npcId));

            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            lock (_lockObject)
            {
                if (_npcProfiles.ContainsKey(npcId))
                {
                    _logger.LogWarning($"NPC ID '{npcId}' zaten mevcut. Güncelleniyor...");
                    _npcProfiles[npcId] = profile;
                }
                else;
                {
                    _npcProfiles.Add(npcId, profile);
                }

                // Varsayılan bir günlük plan oluştur;
                var defaultSchedule = GenerateDefaultSchedule(profile);
                _activeSchedules[npcId] = defaultSchedule;

                _logger.LogDebug($"NPC '{npcId}' eklendi: {profile.Name} - {profile.Occupation}");
            }
        }

        /// <summary>
        /// NPC için günlük plan oluşturur;
        /// </summary>
        /// <param name="npcId">NPC kimliği</param>
        /// <param name="date">Plan tarihi</param>
        /// <returns>Oluşturulan plan</returns>
        public DailySchedule GenerateDailySchedule(string npcId, DateTime date)
        {
            ValidateNpcId(npcId);

            var profile = _npcProfiles[npcId];
            var schedule = new DailySchedule(npcId, date);

            try
            {
                // Temel rutin aktiviteleri ekle;
                AddBasicRoutineActivities(schedule, profile);

                // Mesleğe özgü aktiviteleri ekle;
                AddOccupationActivities(schedule, profile);

                // Rastgele sosyal aktiviteler ekle;
                AddSocialActivities(schedule, profile);

                // Acil durum/rastgele olaylar ekle;
                AddRandomEvents(schedule, profile);

                // Zaman çakışmalarını çöz;
                ResolveTimeConflicts(schedule);

                // Planı optimize et;
                OptimizeSchedule(schedule);

                // Aktif planlara ekle;
                lock (_lockObject)
                {
                    _activeSchedules[npcId] = schedule;
                }

                _logger.LogDebug($"NPC '{npcId}' için günlük plan oluşturuldu: {date:yyyy-MM-dd}");
                return schedule;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Plan oluşturma hatası (NPC: {npcId}): {ex.Message}", ex);
                throw new SchedulePlannerException("Günlük plan oluşturma hatası", ex);
            }
        }

        /// <summary>
        /// Planlayıcıyı başlatır;
        /// </summary>
        public void Start()
        {
            if (!_isInitialized)
                throw new SchedulePlannerException("Planlayıcı başlatılmamış. Önce InitializeAsync çağrılmalı.");

            if (_isRunning)
            {
                _logger.LogWarning("Planlayıcı zaten çalışıyor.");
                return;
            }

            _isRunning = true;
            _schedulerTask = Task.Run(async () => await SchedulerLoopAsync());

            _logger.LogInformation("SchedulePlanner başlatıldı.");
        }

        /// <summary>
        /// Planlayıcıyı durdurur;
        /// </summary>
        public async Task StopAsync()
        {
            if (!_isRunning)
                return;

            _isRunning = false;

            if (_schedulerTask != null && !_schedulerTask.IsCompleted)
            {
                await _schedulerTask;
            }

            _logger.LogInformation("SchedulePlanner durduruldu.");
        }

        /// <summary>
        /// Belirli bir NPC'nin mevcut aktivitesini getirir;
        /// </summary>
        /// <param name="npcId">NPC kimliği</param>
        /// <returns>Mevcut aktivite veya null</returns>
        public ScheduledActivity GetCurrentActivity(string npcId)
        {
            ValidateNpcId(npcId);

            lock (_lockObject)
            {
                if (!_activeSchedules.TryGetValue(npcId, out var schedule))
                    return null;

                return schedule.GetActivityAtTime(CurrentGameTime);
            }
        }

        /// <summary>
        /// NPC'nin bir sonraki aktivitesini getirir;
        /// </summary>
        /// <param name="npcId">NPC kimliği</param>
        /// <returns>Sonraki aktivite veya null</returns>
        public ScheduledActivity GetNextActivity(string npcId)
        {
            ValidateNpcId(npcId);

            lock (_lockObject)
            {
                if (!_activeSchedules.TryGetValue(npcId, out var schedule))
                    return null;

                return schedule.GetNextActivity(CurrentGameTime);
            }
        }

        /// <summary>
        NPC aktivitesini manuel olarak değiştirir;
        </summary>
        /// <param name="npcId">NPC kimliği</param>
        /// <param name="newActivity">Yeni aktivite</param>
        /// <param name="reason">Değişiklik nedeni</param>
        public bool ChangeActivity(string npcId, ActivityTemplate newActivity, string reason = "Manual override")
        {
            ValidateNpcId(npcId);

            try
            {
                lock (_lockObject)
                {
                    if (!_activeSchedules.TryGetValue(npcId, out var schedule))
                        return false;

                    var currentActivity = schedule.GetActivityAtTime(CurrentGameTime);
                    if (currentActivity != null)
                    {
                        currentActivity.MarkInterrupted(reason);
                    }

                    var scheduledActivity = new ScheduledActivity(
                        newActivity,
                        CurrentGameTime,
                        CurrentGameTime.Add(newActivity.EstimatedDuration),
                        ActivityPriority.High;
                    );

                    schedule.InsertActivity(scheduledActivity, true);

                    // Blackboard'ı güncelle;
                    _blackboard.SetValue($"NPC_{npcId}_CurrentActivity", newActivity.Name);
                    _blackboard.SetValue($"NPC_{npcId}_ActivityChanged", true);
                    _blackboard.SetValue($"NPC_{npcId}_ChangeReason", reason);

                    _logger.LogInformation($"NPC '{npcId}' aktivitesi değiştirildi: {newActivity.Name} - Nedeni: {reason}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Aktivite değiştirme hatası (NPC: {npcId}): {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Tüm NPC'lerin mevcut durumunu getirir;
        /// </summary>
        /// <returns>NPC durum raporu</returns>
        public NPCStatusReport GetStatusReport()
        {
            lock (_lockObject)
            {
                var report = new NPCStatusReport;
                {
                    ReportTime = DateTime.UtcNow,
                    GameTime = CurrentGameTime,
                    ActiveNpcCount = _npcProfiles.Count,
                    NpcStatuses = new List<NPCStatus>()
                };

                foreach (var kvp in _npcProfiles)
                {
                    var npcId = kvp.Key;
                    var profile = kvp.Value;
                    var currentActivity = GetCurrentActivity(npcId);
                    var nextActivity = GetNextActivity(npcId);

                    var status = new NPCStatus;
                    {
                        NpcId = npcId,
                        Name = profile.Name,
                        Occupation = profile.Occupation,
                        CurrentActivity = currentActivity?.Activity?.Name ?? "Idle",
                        CurrentLocation = currentActivity?.Activity?.DefaultLocation ?? "Unknown",
                        NextActivity = nextActivity?.Activity?.Name,
                        NextActivityTime = nextActivity?.StartTime,
                        EnergyLevel = profile.GetEnergyLevel(CurrentGameTime),
                        Mood = profile.CurrentMood,
                        LastActivityChange = currentActivity?.StartTime ?? DateTime.MinValue;
                    };

                    report.NpcStatuses.Add(status);
                }

                return report;
            }
        }

        #endregion;

        #region Private Methods;

        /// <summary>
        /// Planlayıcı ana döngüsü;
        /// </summary>
        private async Task SchedulerLoopAsync()
        {
            _logger.LogInformation("Planlayıcı döngüsü başlatıldı.");

            while (_isRunning)
            {
                try
                {
                    lock (_lockObject)
                    {
                        // Her NPC için mevcut aktiviteyi kontrol et;
                        foreach (var npcId in _npcProfiles.Keys.ToList())
                        {
                            UpdateNpcActivity(npcId);
                        }

                        // Oyun saati ilerlet (simülasyon için)
                        if (_configuration.TimeAcceleration > 0)
                        {
                            _currentGameTime = _currentGameTime.AddMinutes(_configuration.TimeAcceleration);
                        }
                    }

                    // Bekleme süresi;
                    await Task.Delay(_configuration.UpdateInterval);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Planlayıcı döngü hatası: {ex.Message}", ex);
                    await Task.Delay(1000); // Hata durumunda kısa bekleme;
                }
            }

            _logger.LogInformation("Planlayıcı döngüsü sonlandırıldı.");
        }

        /// <summary>
        /// NPC'nin mevcut aktivitesini günceller;
        /// </summary>
        private void UpdateNpcActivity(string npcId)
        {
            if (!_activeSchedules.TryGetValue(npcId, out var schedule))
                return;

            var currentActivity = schedule.GetActivityAtTime(CurrentGameTime);
            var previousActivity = _blackboard.GetValue<ScheduledActivity>($"NPC_{npcId}_PreviousActivity");

            if (currentActivity != null && currentActivity != previousActivity)
            {
                // Yeni aktivite başladı;
                OnActivityStarted(npcId, currentActivity);
            }
            else if (currentActivity == null && previousActivity != null)
            {
                // Aktivite sona erdi;
                OnActivityEnded(npcId, previousActivity);
            }

            // Aktivite ilerlemesini güncelle;
            if (currentActivity != null)
            {
                UpdateActivityProgress(npcId, currentActivity);
            }
        }

        /// <summary>
        /// Aktivite başlangıç işlemleri;
        /// </summary>
        private void OnActivityStarted(string npcId, ScheduledActivity activity)
        {
            _logger.LogDebug($"NPC '{npcId}' aktivite başladı: {activity.Activity.Name}");

            // Blackboard güncellemesi;
            _blackboard.SetValue($"NPC_{npcId}_CurrentActivity", activity.Activity.Name);
            _blackboard.SetValue($"NPC_{npcId}_PreviousActivity", activity);
            _blackboard.SetValue($"NPC_{npcId}_ActivityStartTime", CurrentGameTime);
            _blackboard.SetValue($"NPC_{npcId}_IsBusy", true);

            // Davranış ağacı tetikleyicisi;
            _behaviorTree.SetVariable($"StartActivity_{activity.Activity.Name}", true);

            // NPC profili güncellemesi;
            if (_npcProfiles.TryGetValue(npcId, out var profile))
            {
                profile.OnActivityStarted(activity.Activity);
            }
        }

        /// <summary>
        /// Aktivite bitiş işlemleri;
        /// </summary>
        private void OnActivityEnded(string npcId, ScheduledActivity activity)
        {
            _logger.LogDebug($"NPC '{npcId}' aktivite bitti: {activity.Activity.Name}");

            activity.MarkCompleted();

            // Blackboard güncellemesi;
            _blackboard.SetValue($"NPC_{npcId}_CurrentActivity", "Idle");
            _blackboard.SetValue($"NPC_{npcId}_PreviousActivity", null);
            _blackboard.SetValue($"NPC_{npcId}_IsBusy", false);

            // NPC profili güncellemesi;
            if (_npcProfiles.TryGetValue(npcId, out var profile))
            {
                profile.OnActivityCompleted(activity.Activity);
            }
        }

        /// <summary>
        /// Aktivite ilerlemesini günceller;
        /// </summary>
        private void UpdateActivityProgress(string npcId, ScheduledActivity activity)
        {
            var progress = activity.CalculateProgress(CurrentGameTime);
            _blackboard.SetValue($"NPC_{npcId}_ActivityProgress", progress);

            // Enerji ve ruh hali güncellemesi;
            if (_npcProfiles.TryGetValue(npcId, out var profile))
            {
                profile.UpdateDuringActivity(activity.Activity, progress);
            }
        }

        /// <summary>
        /// Varsayılan günlük plan oluşturur;
        /// </summary>
        private DailySchedule GenerateDefaultSchedule(NPCProfile profile)
        {
            var schedule = new DailySchedule(profile.Id, DateTime.Today);

            // Temel yaşam aktiviteleri;
            var basicActivities = new[]
            {
                new ScheduledActivity(
                    GetActivityTemplate("Sleep"),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 23, 0, 0),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 7, 0, 0),
                    ActivityPriority.VeryHigh;
                ),
                new ScheduledActivity(
                    GetActivityTemplate("EatBreakfast"),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 7, 30, 0),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 8, 0, 0),
                    ActivityPriority.High;
                ),
                new ScheduledActivity(
                    GetActivityTemplate("EatLunch"),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 13, 0, 0),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 13, 30, 0),
                    ActivityPriority.High;
                ),
                new ScheduledActivity(
                    GetActivityTemplate("EatDinner"),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 19, 0, 0),
                    new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, 19, 30, 0),
                    ActivityPriority.High;
                )
            };

            foreach (var activity in basicActivities)
            {
                schedule.AddActivity(activity);
            }

            return schedule;
        }

        /// <summary>
        /// Temel rutin aktivitelerini ekler;
        /// </summary>
        private void AddBasicRoutineActivities(DailySchedule schedule, NPCProfile profile)
        {
            // Uyku;
            var sleepHours = profile.GetPreferredSleepHours();
            var sleepActivity = new ScheduledActivity(
                GetActivityTemplate("Sleep"),
                schedule.Date.AddHours(sleepHours.Start),
                schedule.Date.AddHours(sleepHours.End),
                ActivityPriority.VeryHigh;
            );
            schedule.AddActivity(sleepActivity);

            // Yemek zamanları;
            AddMealActivities(schedule, profile);
        }

        /// <summary>
        /// Yemek aktivitelerini ekler;
        /// </summary>
        private void AddMealActivities(DailySchedule schedule, NPCProfile profile)
        {
            var mealTimes = profile.GetPreferredMealTimes();

            var breakfast = new ScheduledActivity(
                GetActivityTemplate("EatBreakfast"),
                schedule.Date.Add(mealTimes.Breakfast),
                schedule.Date.Add(mealTimes.Breakfast.Add(TimeSpan.FromMinutes(30))),
                ActivityPriority.High;
            );
            schedule.AddActivity(breakfast);

            var lunch = new ScheduledActivity(
                GetActivityTemplate("EatLunch"),
                schedule.Date.Add(mealTimes.Lunch),
                schedule.Date.Add(mealTimes.Lunch.Add(TimeSpan.FromMinutes(45))),
                ActivityPriority.High;
            );
            schedule.AddActivity(lunch);

            var dinner = new ScheduledActivity(
                GetActivityTemplate("EatDinner"),
                schedule.Date.Add(mealTimes.Dinner),
                schedule.Date.Add(mealTimes.Dinner.Add(TimeSpan.FromMinutes(60))),
                ActivityPriority.High;
            );
            schedule.AddActivity(dinner);
        }

        /// <summary>
        /// Mesleğe özgü aktiviteleri ekler;
        /// </summary>
        private void AddOccupationActivities(DailySchedule schedule, NPCProfile profile)
        {
            if (string.IsNullOrEmpty(profile.Occupation))
                return;

            var occupationTemplates = GetOccupationActivityTemplates(profile.Occupation);
            if (occupationTemplates == null || occupationTemplates.Count == 0)
                return;

            var workStart = schedule.Date.AddHours(9); // Varsayılan iş başlangıcı: 09:00;
            var workEnd = schedule.Date.AddHours(17); // Varsayılan iş bitişi: 17:00;

            // İş aktivitelerini rastgele seç ve zamanla;
            var workDuration = workEnd - workStart;
            var availableTime = workDuration;
            var currentTime = workStart;

            while (availableTime.TotalMinutes > 30) // En az 30 dakika boşluk kaldıysa;
            {
                var template = occupationTemplates[_random.Next(occupationTemplates.Count)];
                var duration = template.EstimatedDuration;

                if (duration <= availableTime)
                {
                    var activity = new ScheduledActivity(
                        template,
                        currentTime,
                        currentTime.Add(duration),
                        ActivityPriority.Medium;
                    );

                    schedule.AddActivity(activity);

                    currentTime = currentTime.Add(duration);
                    availableTime = availableTime.Subtract(duration);

                    // Kısa bir mola ekle;
                    if (_random.NextDouble() < 0.3 && availableTime.TotalMinutes > 15)
                    {
                        var breakDuration = TimeSpan.FromMinutes(_random.Next(5, 15));
                        var breakActivity = new ScheduledActivity(
                            GetActivityTemplate("TakeBreak"),
                            currentTime,
                            currentTime.Add(breakDuration),
                            ActivityPriority.Low;
                        );

                        schedule.AddActivity(breakActivity);

                        currentTime = currentTime.Add(breakDuration);
                        availableTime = availableTime.Subtract(breakDuration);
                    }
                }
                else;
                {
                    break;
                }
            }
        }

        /// <summary>
        /// Sosyal aktiviteleri ekler;
        /// </summary>
        private void AddSocialActivities(DailySchedule schedule, NPCProfile profile)
        {
            if (!profile.IsSocial || _random.NextDouble() > 0.7) // %30 şans;
                return;

            var socialTemplates = GetActivityTemplatesByCategory("Social");
            if (socialTemplates.Count == 0)
                return;

            var template = socialTemplates[_random.Next(socialTemplates.Count)];

            // Akşam saatlerinde sosyal aktivite planla;
            var eveningStart = schedule.Date.AddHours(18 + _random.Next(0, 3)); // 18:00-21:00;
            var activity = new ScheduledActivity(
                template,
                eveningStart,
                eveningStart.Add(template.EstimatedDuration),
                ActivityPriority.Medium;
            );

            schedule.AddActivity(activity);
        }

        /// <summary>
        /// Rastgele olaylar ekler;
        /// </summary>
        private void AddRandomEvents(DailySchedule schedule, NPCProfile profile)
        {
            if (_random.NextDouble() > 0.2) // %20 şans;
                return;

            var eventTemplates = GetActivityTemplatesByCategory("RandomEvent");
            if (eventTemplates.Count == 0)
                return;

            var template = eventTemplates[_random.Next(eventTemplates.Count)];

            // Gün içinde rastgele bir zaman seç;
            var randomHour = _random.Next(8, 20);
            var randomMinute = _random.Next(0, 60);
            var eventTime = schedule.Date.AddHours(randomHour).AddMinutes(randomMinute);

            var activity = new ScheduledActivity(
                template,
                eventTime,
                eventTime.Add(template.EstimatedDuration),
                ActivityPriority.Low;
            );

            schedule.AddActivity(activity);
        }

        /// <summary>
        /// Zaman çakışmalarını çözer;
        /// </summary>
        private void ResolveTimeConflicts(DailySchedule schedule)
        {
            var activities = schedule.GetAllActivities().OrderBy(a => a.StartTime).ToList();

            for (int i = 0; i < activities.Count - 1; i++)
            {
                var current = activities[i];
                var next = activities[i + 1];

                if (current.EndTime > next.StartTime)
                {
                    // Çakışma var, sonraki aktiviteyi kaydır;
                    var overlap = current.EndTime - next.StartTime;
                    next.StartTime = next.StartTime.Add(overlap);
                    next.EndTime = next.EndTime.Add(overlap);

                    _logger.LogDebug($"Zaman çakışması çözüldü: {current.Activity.Name} → {next.Activity.Name}");
                }
            }
        }

        /// <summary>
        /// Planı optimize eder;
        /// </summary>
        private void OptimizeSchedule(DailySchedule schedule)
        {
            // Boş zamanları doldur;
            FillGaps(schedule);

            // Aktivite sıralamasını optimize et;
            OptimizeActivityOrder(schedule);

            // Seyahat zamanlarını hesaba kat;
            AddTravelTime(schedule);
        }

        /// <summary>
        /// Boş zaman aralıklarını doldurur;
        /// </summary>
        private void FillGaps(DailySchedule schedule)
        {
            var activities = schedule.GetAllActivities().OrderBy(a => a.StartTime).ToList();

            for (int i = 0; i < activities.Count - 1; i++)
            {
                var gap = activities[i + 1].StartTime - activities[i].EndTime;

                if (gap.TotalMinutes > 30) // 30 dakikadan fazla boşluk varsa;
                {
                    var idleActivity = new ScheduledActivity(
                        GetActivityTemplate("Idle"),
                        activities[i].EndTime,
                        activities[i + 1].StartTime,
                        ActivityPriority.VeryLow;
                    );

                    schedule.AddActivity(idleActivity);
                }
            }
        }

        /// <summary>
        /// Aktivite sıralamasını optimize eder;
        /// </summary>
        private void OptimizeActivityOrder(DailySchedule schedule)
        {
            // Aynı lokasyondaki aktiviteleri grupla;
            var activitiesByLocation = schedule.GetAllActivities()
                .GroupBy(a => a.Activity.DefaultLocation)
                .ToList();

            // Lokasyona göre yeniden sırala;
            var optimizedActivities = new List<ScheduledActivity>();
            var currentTime = schedule.Date;

            foreach (var locationGroup in activitiesByLocation)
            {
                foreach (var activity in locationGroup.OrderBy(a => a.StartTime))
                {
                    // Aktiviteyi optimize edilmiş zaman dilimine taşı;
                    activity.StartTime = currentTime;
                    activity.EndTime = currentTime.Add(activity.Activity.EstimatedDuration);
                    optimizedActivities.Add(activity);
                    currentTime = activity.EndTime;
                }
            }

            // Schedule'ı güncelle;
            schedule.ClearActivities();
            foreach (var activity in optimizedActivities)
            {
                schedule.AddActivity(activity);
            }
        }

        /// <summary>
        /// Seyahat zamanlarını ekler;
        /// </summary>
        private void AddTravelTime(DailySchedule schedule)
        {
            var activities = schedule.GetAllActivities().OrderBy(a => a.StartTime).ToList();

            for (int i = 0; i < activities.Count - 1; i++)
            {
                var current = activities[i];
                var next = activities[i + 1];

                if (current.Activity.DefaultLocation != next.Activity.DefaultLocation)
                {
                    // Lokasyon değişikliği var, seyahat zamanı ekle;
                    var travelTime = CalculateTravelTime(
                        current.Activity.DefaultLocation,
                        next.Activity.DefaultLocation;
                    );

                    if (travelTime.TotalMinutes > 0)
                    {
                        var travelActivity = new ScheduledActivity(
                            GetActivityTemplate("Travel"),
                            current.EndTime,
                            current.EndTime.Add(travelTime),
                            ActivityPriority.Low;
                        );

                        // Sonraki aktiviteyi seyahat zamanı kadar kaydır;
                        next.StartTime = next.StartTime.Add(travelTime);
                        next.EndTime = next.EndTime.Add(travelTime);

                        schedule.AddActivity(travelActivity);
                    }
                }
            }
        }

        /// <summary>
        /// Seyahat süresini hesaplar;
        /// </summary>
        private TimeSpan CalculateTravelTime(string fromLocation, string toLocation)
        {
            // Basit bir mesafe hesaplama (gerçek uygulamada daha karmaşık olacak)
            var baseTime = TimeSpan.FromMinutes(5); // Varsayılan 5 dakika;

            // Lokasyon türüne göre süre ayarla;
            if (fromLocation == "Home" && toLocation == "Work")
                baseTime = TimeSpan.FromMinutes(30);
            else if (fromLocation.Contains("District") && toLocation.Contains("District"))
                baseTime = TimeSpan.FromMinutes(15);

            // Rastgele varyasyon ekle (±%20)
            var variation = _random.NextDouble() * 0.4 - 0.2; // -0.2 ile +0.2 arası;
            var finalTime = baseTime.TotalMinutes * (1 + variation);

            return TimeSpan.FromMinutes(finalTime);
        }

        /// <summary>
        /// Aktivite şablonlarını yükler;
        /// </summary>
        private async Task LoadActivityTemplatesAsync()
        {
            try
            {
                // Varsayılan aktiviteler;
                var defaultActivities = new List<ActivityTemplate>
                {
                    new ActivityTemplate("Sleep", "Uyku", TimeSpan.FromHours(8), "Home", ActivityCategory.Basic),
                    new ActivityTemplate("EatBreakfast", "Kahvaltı", TimeSpan.FromMinutes(30), "Home", ActivityCategory.Basic),
                    new ActivityTemplate("EatLunch", "Öğle Yemeği", TimeSpan.FromMinutes(45), "Work", ActivityCategory.Basic),
                    new ActivityTemplate("EatDinner", "Akşam Yemeği", TimeSpan.FromMinutes(60), "Home", ActivityCategory.Basic),
                    new ActivityTemplate("Work", "Çalışma", TimeSpan.FromHours(4), "Work", ActivityCategory.Occupation),
                    new ActivityTemplate("Meeting", "Toplantı", TimeSpan.FromHours(1), "Office", ActivityCategory.Occupation),
                    new ActivityTemplate("Socialize", "Sosyalleşme", TimeSpan.FromHours(2), "Pub", ActivityCategory.Social),
                    new ActivityTemplate("Exercise", "Egzersiz", TimeSpan.FromHours(1), "Gym", ActivityCategory.Health),
                    new ActivityTemplate("Idle", "Boş Zaman", TimeSpan.FromMinutes(30), "Current", ActivityCategory.Basic),
                    new ActivityTemplate("Travel", "Seyahat", TimeSpan.FromMinutes(15), "Road", ActivityCategory.Basic),
                    new ActivityTemplate("TakeBreak", "Mola", TimeSpan.FromMinutes(10), "Current", ActivityCategory.Basic)
                };

                _activityTemplates["Default"] = defaultActivities;

                // Kategoriye göre grupla;
                foreach (var activity in defaultActivities)
                {
                    if (!_activityTemplates.ContainsKey(activity.Category.ToString()))
                    {
                        _activityTemplates[activity.Category.ToString()] = new List<ActivityTemplate>();
                    }
                    _activityTemplates[activity.Category.ToString()].Add(activity);
                }

                _logger.LogInformation($"{defaultActivities.Count} varsayılan aktivite şablonu yüklendi.");

                // Dosyadan ek şablonlar yükle (gerçek uygulamada)
                await LoadAdditionalTemplatesFromFileAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Aktivite şablonları yükleme hatası: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Dosyadan ek aktivite şablonları yükler;
        /// </summary>
        private async Task LoadAdditionalTemplatesFromFileAsync()
        {
            // Gerçek uygulamada bu dosya okuma işlemi yapılacak;
            // Şimdilik boş bırakıldı;
            await Task.CompletedTask;
        }

        /// <summary>
        /// Aktivite şablonu getirir;
        /// </summary>
        private ActivityTemplate GetActivityTemplate(string name)
        {
            return _activityTemplates["Default"]
                .FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? new ActivityTemplate(name, name, TimeSpan.FromHours(1), "Unknown", ActivityCategory.Basic);
        }

        /// <summary>
        /// Kategoriye göre aktivite şablonları getirir;
        /// </summary>
        private List<ActivityTemplate> GetActivityTemplatesByCategory(string category)
        {
            return _activityTemplates.ContainsKey(category)
                ? _activityTemplates[category]
                : new List<ActivityTemplate>();
        }

        /// <summary>
        /// Mesleğe özgü aktivite şablonları getirir;
        /// </summary>
        private List<ActivityTemplate> GetOccupationActivityTemplates(string occupation)
        {
            // Meslek bazlı şablonlar (gerçek uygulamada daha kapsamlı olacak)
            var occupationTemplates = new Dictionary<string, List<ActivityTemplate>>
            {
                ["Merchant"] = new List<ActivityTemplate>
                {
                    new ActivityTemplate("Trade", "Ticaret", TimeSpan.FromHours(2), "Market", ActivityCategory.Occupation),
                    new ActivityTemplate("Restock", "Stok Yenileme", TimeSpan.FromHours(1), "Warehouse", ActivityCategory.Occupation),
                    new ActivityTemplate("Haggle", "Pazarlık", TimeSpan.FromMinutes(30), "Shop", ActivityCategory.Occupation)
                },
                ["Guard"] = new List<ActivityTemplate>
                {
                    new ActivityTemplate("Patrol", "Devriye", TimeSpan.FromHours(3), "Street", ActivityCategory.Occupation),
                    new ActivityTemplate("GuardPost", "Nöbet", TimeSpan.FromHours(4), "Gate", ActivityCategory.Occupation),
                    new ActivityTemplate("Training", "Eğitim", TimeSpan.FromHours(1), "Barracks", ActivityCategory.Occupation)
                },
                ["Blacksmith"] = new List<ActivityTemplate>
                {
                    new ActivityTemplate("Forge", "Dövme", TimeSpan.FromHours(2), "Forge", ActivityCategory.Occupation),
                    new ActivityTemplate("Repair", "Tamirat", TimeSpan.FromHours(1), "Workshop", ActivityCategory.Occupation),
                    new ActivityTemplate("Smithing", "Demircilik", TimeSpan.FromHours(3), "Smithy", ActivityCategory.Occupation)
                }
            };

            return occupationTemplates.ContainsKey(occupation)
                ? occupationTemplates[occupation]
                : new List<ActivityTemplate>();
        }

        /// <summary>
        /// Blackboard değişkenlerini başlatır;
        /// </summary>
        private void InitializeBlackboardVariables()
        {
            _blackboard.SetValue("SchedulePlanner_Active", true);
            _blackboard.SetValue("SchedulePlanner_GameTime", CurrentGameTime);
            _blackboard.SetValue("SchedulePlanner_TotalNPCs", 0);
            _blackboard.SetValue("SchedulePlanner_LastUpdate", DateTime.UtcNow);
        }

        /// <summary>
        /// Davranış ağacını yapılandırır;
        /// </summary>
        private void ConfigureBehaviorTree()
        {
            // SchedulePlanner ile ilgili davranış ağacı düğümleri eklenebilir;
            // Bu kısım davranış ağacı entegrasyonu için;
        }

        /// <summary>
        /// NPC ID doğrulaması yapar;
        /// </summary>
        private void ValidateNpcId(string npcId)
        {
            if (string.IsNullOrWhiteSpace(npcId))
                throw new ArgumentException("NPC ID boş olamaz.", nameof(npcId));

            lock (_lockObject)
            {
                if (!_npcProfiles.ContainsKey(npcId))
                    throw new SchedulePlannerException($"NPC '{npcId}' bulunamadı.");
            }
        }

        #endregion;

        #region IDisposable Implementation;

        private bool _disposed = false;

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
            if (_disposed)
                return;

            if (disposing)
            {
                // Yönetilen kaynakları serbest bırak;
                if (_isRunning)
                {
                    StopAsync().Wait();
                }

                lock (_lockObject)
                {
                    _npcProfiles?.Clear();
                    _activeSchedules?.Clear();
                    _activityTemplates?.Clear();
                }
            }

            _disposed = true;
        }

        ~SchedulePlanner()
        {
            Dispose(false);
        }

        #endregion;
    }

    #region Supporting Classes and Enums;

    /// <summary>
    /// NPC profili sınıfı;
    /// </summary>
    public class NPCProfile;
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Occupation { get; set; }
        public int Age { get; set; }
        public string Personality { get; set; }
        public bool IsSocial { get; set; }
        public float Energy { get; set; } = 100f;
        public float Mood { get; set; } = 50f;
        public Dictionary<string, float> Preferences { get; set; } = new Dictionary<string, float>();

        /// <summary>
        /// Mevcut ruh hali;
        /// </summary>
        public string CurrentMood => GetMoodDescription(Mood);

        /// <summary>
        /// Tercih edilen uyku saatleri;
        /// </summary>
        public (int Start, int End) GetPreferredSleepHours()
        {
            // Personality'ye göre uyku saatleri;
            return Personality switch;
            {
                "EarlyBird" => (22, 6),
                "NightOwl" => (1, 9),
                "Worker" => (23, 7),
                _ => (23, 7) // Varsayılan;
            };
        }

        /// <summary>
        /// Tercih edilen yemek saatleri;
        /// </summary>
        public MealTimes GetPreferredMealTimes()
        {
            return new MealTimes;
            {
                Breakfast = TimeSpan.FromHours(7.5), // 07:30;
                Lunch = TimeSpan.FromHours(13),      // 13:00;
                Dinner = TimeSpan.FromHours(19)      // 19:00;
            };
        }

        /// <summary>
        /// Belirli bir zamandaki enerji seviyesi;
        /// </summary>
        public float GetEnergyLevel(DateTime time)
        {
            var hour = time.Hour;

            // Günün saatine göre enerji seviyesi;
            if (hour >= 22 || hour <= 6)
                return Energy * 0.3f; // Düşük enerji (uyku saati)
            else if (hour >= 13 && hour <= 15)
                return Energy * 0.7f; // Öğleden sonra düşüşü;
            else if (hour >= 9 && hour <= 12)
                return Energy * 0.9f; // Sabah en yüksek;
            else;
                return Energy * 0.8f; // Normal;
        }

        /// <summary>
        /// Aktivite başladığında çağrılır;
        /// </summary>
        public void OnActivityStarted(ActivityTemplate activity)
        {
            // Aktivite tercihlerine göre ruh hali değişimi;
            if (Preferences.ContainsKey(activity.Name))
            {
                Mood += Preferences[activity.Name] * 10f;
                Mood = Math.Clamp(Mood, 0f, 100f);
            }
        }

        /// <summary>
        /// Aktivite tamamlandığında çağrılır;
        /// </summary>
        public void OnActivityCompleted(ActivityTemplate activity)
        {
            // Aktivite sonrası enerji tüketimi;
            var energyCost = activity.EstimatedDuration.TotalHours * 5f;
            Energy = Math.Max(0, Energy - (float)energyCost);

            // Enerji yenilenmesi;
            if (activity.Category == ActivityCategory.Basic && activity.Name == "Sleep")
            {
                Energy = 100f; // Uyku ile tam enerji;
            }
        }

        /// <summary>
        /// Aktivite sırasında güncelleme;
        /// </summary>
        public void UpdateDuringActivity(ActivityTemplate activity, float progress)
        {
            // Aktivite ilerledikçe enerji tüketimi;
            var energyDrain = 0.1f * progress;
            Energy = Math.Max(0, Energy - energyDrain);
        }

        private string GetMoodDescription(float moodValue)
        {
            return moodValue switch;
            {
                > 80 => "Very Happy",
                > 60 => "Happy",
                > 40 => "Neutral",
                > 20 => "Sad",
                _ => "Very Sad"
            };
        }
    }

    /// <summary>
    /// Yemek saatleri;
    /// </summary>
    public class MealTimes;
    {
        public TimeSpan Breakfast { get; set; }
        public TimeSpan Lunch { get; set; }
        public TimeSpan Dinner { get; set; }
    }

    /// <summary>
    /// Aktivite şablonu;
    /// </summary>
    public class ActivityTemplate;
    {
        public string Name { get; }
        public string DisplayName { get; }
        public TimeSpan EstimatedDuration { get; }
        public string DefaultLocation { get; }
        public ActivityCategory Category { get; }
        public Dictionary<string, object> Parameters { get; }

        public ActivityTemplate(string name, string displayName, TimeSpan duration,
                              string defaultLocation, ActivityCategory category)
        {
            Name = name;
            DisplayName = displayName;
            EstimatedDuration = duration;
            DefaultLocation = defaultLocation;
            Category = category;
            Parameters = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Planlanmış aktivite;
    /// </summary>
    public class ScheduledActivity;
    {
        public ActivityTemplate Activity { get; }
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
        public ActivityPriority Priority { get; }
        public ActivityStatus Status { get; private set; }
        public DateTime? ActualStartTime { get; private set; }
        public DateTime? ActualEndTime { get; private set; }
        public string InterruptionReason { get; private set; }

        public ScheduledActivity(ActivityTemplate activity, DateTime startTime,
                               DateTime endTime, ActivityPriority priority)
        {
            Activity = activity ?? throw new ArgumentNullException(nameof(activity));
            StartTime = startTime;
            EndTime = endTime;
            Priority = priority;
            Status = ActivityStatus.Scheduled;
        }

        /// <summary>
        /// Aktivite ilerlemesini hesaplar;
        /// </summary>
        public float CalculateProgress(DateTime currentTime)
        {
            if (currentTime < StartTime)
                return 0f;

            if (currentTime > EndTime)
                return 1f;

            var totalDuration = (EndTime - StartTime).TotalMinutes;
            var elapsed = (currentTime - StartTime).TotalMinutes;

            return (float)(elapsed / totalDuration);
        }

        /// <summary>
        /// Aktiviteyi başlatır;
        /// </summary>
        public void MarkStarted()
        {
            if (Status == ActivityStatus.Scheduled)
            {
                Status = ActivityStatus.InProgress;
                ActualStartTime = DateTime.Now;
            }
        }

        /// <summary>
        /// Aktiviteyi tamamlar;
        /// </summary>
        public void MarkCompleted()
        {
            Status = ActivityStatus.Completed;
            ActualEndTime = DateTime.Now;
        }

        /// <summary>
        /// Aktiviteyi iptal eder;
        /// </summary>
        public void MarkCancelled(string reason)
        {
            Status = ActivityStatus.Cancelled;
            ActualEndTime = DateTime.Now;
            InterruptionReason = reason;
        }

        /// <summary>
        /// Aktivitenin kesintiye uğradığını işaretler;
        /// </summary>
        public void MarkInterrupted(string reason)
        {
            Status = ActivityStatus.Interrupted;
            InterruptionReason = reason;
        }

        /// <summary>
        /// Aktivitenin geçerli olup olmadığını kontrol eder;
        /// </summary>
        public bool IsValid()
        {
            return StartTime < EndTime &&
                   Activity != null &&
                   !string.IsNullOrEmpty(Activity.Name);
        }

        public override string ToString()
        {
            return $"{Activity.DisplayName}: {StartTime:HH:mm} - {EndTime:HH:mm} ({Status})";
        }
    }

    /// <summary>
    /// Günlük plan;
    /// </summary>
    public class DailySchedule;
    {
        public string NpcId { get; }
        public DateTime Date { get; }
        private List<ScheduledActivity> _activities;

        public IReadOnlyList<ScheduledActivity> Activities => _activities.AsReadOnly();

        public DailySchedule(string npcId, DateTime date)
        {
            NpcId = npcId ?? throw new ArgumentNullException(nameof(npcId));
            Date = date.Date;
            _activities = new List<ScheduledActivity>();
        }

        /// <summary>
        /// Aktivite ekler;
        /// </summary>
        public void AddActivity(ScheduledActivity activity)
        {
            if (activity == null)
                throw new ArgumentNullException(nameof(activity));

            if (!activity.IsValid())
                throw new ArgumentException("Geçersiz aktivite.");

            _activities.Add(activity);
            SortActivities();
        }

        /// <summary>
        /// Aktiviteyi belirtilen konuma ekler;
        /// </summary>
        public void InsertActivity(ScheduledActivity activity, bool allowOverlap = false)
        {
            if (activity == null)
                throw new ArgumentNullException(nameof(activity));

            if (!allowOverlap && HasOverlap(activity))
            {
                throw new SchedulePlannerException("Aktivite çakışması var.");
            }

            _activities.Add(activity);
            SortActivities();
        }

        /// <summary>
        /// Belirli bir zamandaki aktiviteyi getirir;
        /// </summary>
        public ScheduledActivity GetActivityAtTime(DateTime time)
        {
            return _activities.FirstOrDefault(a =>
                time >= a.StartTime && time <= a.EndTime &&
                a.Status != ActivityStatus.Cancelled);
        }

        /// <summary>
        /// Bir sonraki aktiviteyi getirir;
        /// </summary>
        public ScheduledActivity GetNextActivity(DateTime fromTime)
        {
            return _activities;
                .Where(a => a.StartTime > fromTime && a.Status == ActivityStatus.Scheduled)
                .OrderBy(a => a.StartTime)
                .FirstOrDefault();
        }

        /// <summary>
        /// Tüm aktiviteleri getirir;
        /// </summary>
        public List<ScheduledActivity> GetAllActivities()
        {
            return new List<ScheduledActivity>(_activities);
        }

        /// <summary>
        /// Tüm aktiviteleri temizler;
        /// </summary>
        public void ClearActivities()
        {
            _activities.Clear();
        }

        /// <summary>
        /// Aktivite çakışması kontrolü;
        /// </summary>
        private bool HasOverlap(ScheduledActivity newActivity)
        {
            return _activities.Any(existing =>
                newActivity.StartTime < existing.EndTime &&
                newActivity.EndTime > existing.StartTime);
        }

        /// <summary>
        /// Aktiviteleri zamana göre sıralar;
        /// </summary>
        private void SortActivities()
        {
            _activities = _activities;
                .OrderBy(a => a.StartTime)
                .ThenByDescending(a => a.Priority)
                .ToList();
        }
    }

    /// <summary>
    /// Planlama yapılandırması;
    /// </summary>
    public class ScheduleConfiguration;
    {
        public int UpdateInterval { get; set; } = 1000; // ms;
        public int TimeAcceleration { get; set; } = 1; // Dakika cinsinden;
        public bool EnableRandomEvents { get; set; } = true;
        public bool EnableSocialActivities { get; set; } = true;
        public int MaxActivitiesPerDay { get; set; } = 15;
        public TimeSpan MinActivityDuration { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan MaxActivityDuration { get; set; } = TimeSpan.FromHours(4);
    }

    /// <summary>
    /// NPC durum raporu;
    /// </summary>
    public class NPCStatusReport;
    {
        public DateTime ReportTime { get; set; }
        public DateTime GameTime { get; set; }
        public int ActiveNpcCount { get; set; }
        public List<NPCStatus> NpcStatuses { get; set; }
    }

    /// <summary>
    /// NPC durumu;
    /// </summary>
    public class NPCStatus;
    {
        public string NpcId { get; set; }
        public string Name { get; set; }
        public string Occupation { get; set; }
        public string CurrentActivity { get; set; }
        public string CurrentLocation { get; set; }
        public string NextActivity { get; set; }
        public DateTime? NextActivityTime { get; set; }
        public float EnergyLevel { get; set; }
        public string Mood { get; set; }
        public DateTime LastActivityChange { get; set; }
    }

    /// <summary>
    /// Aktivite kategorileri;
    /// </summary>
    public enum ActivityCategory;
    {
        Basic,
        Occupation,
        Social,
        Health,
        RandomEvent,
        Travel;
    }

    /// <summary>
    /// Aktivite öncelik seviyeleri;
    /// </summary>
    public enum ActivityPriority;
    {
        VeryLow = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        VeryHigh = 4,
        Critical = 5;
    }

    /// <summary>
    /// Aktivite durumları;
    /// </summary>
    public enum ActivityStatus;
    {
        Scheduled,
        InProgress,
        Completed,
        Cancelled,
        Interrupted,
        Failed;
    }

    /// <summary>
    /// SchedulePlanner özel istisnası;
    /// </summary>
    public class SchedulePlannerException : Exception
    {
        public SchedulePlannerException(string message) : base(message) { }
        public SchedulePlannerException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    #endregion;
}
