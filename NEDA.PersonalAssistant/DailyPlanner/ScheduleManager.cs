// Basic usage;
var scheduleManager = new ScheduleManager();
await scheduleManager.StartAsync();

// Schedule a task to run in 5 minutes;
await scheduleManager.ScheduleDelayedTaskAsync(
    "MyTask",
    async token =>
    {
        Console.WriteLine("Task executed!");
        await Task.Delay(1000, token);
    },
    TimeSpan.FromMinutes(5));

// Schedule a daily task at 2:30 AM;
await scheduleManager.ScheduleDailyAsync(
    "DailyReport",
    async token =>
    {
        await GenerateReportAsync(token);
    },
    hour: 2, minute: 30);

// Schedule a weekly task on Monday at 9 AM;
await scheduleManager.ScheduleWeeklyAsync(
    "WeeklyBackup",
    async token =>
    {
        await PerformBackupAsync(token);
    },
    DayOfWeek.Monday,
    hour: 9, minute: 0);
