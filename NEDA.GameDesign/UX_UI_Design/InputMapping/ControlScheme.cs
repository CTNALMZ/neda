// ControlScheme kullanım örneği;
using NEDA.Core.Configuration.UserProfiles;
using NEDA.Core.Logging;
using NEDA.Core.SystemControl;

var controlScheme = new ControlScheme(
    logger,
    projectManager,
    themeManager);

// Sistem başlatma;
await controlScheme.InitializeAsync(new ControlConfiguration;
{
    DefaultMode = InputMode.Game,
    DefaultProfile = "Default",
    EnableMouseAcceleration = false,
    EnableGamepadVibration = true;
});

// Yeni eylem kaydetme;
var moveAction = await controlScheme.RegisterActionAsync("Move", ActionType.Axis, new ActionConfiguration;
{
    Category = "Movement",
    Priority = ActionPriority.High,
    Sensitivity = 2.0f,
    Deadzone = 0.2f;
});

// Bağlantı ekleme (W tuşu ile ileri hareket)
var wKeyBinding = new InputBinding;
{
    Id = "move_forward_w",
    DeviceId = "keyboard_default",
    InputType = InputType.Key,
    InputCode = 87, // W key;
    DisplayName = "W",
    TriggerOn = TriggerCondition.Hold,
    Sensitivity = 1.0f;
};

await controlScheme.AddBindingAsync("MoveForward", wKeyBinding);

// Bağlantı ekleme (Gamepad sol analog stick)
var gamepadBinding = new InputBinding;
{
    Id = "move_gamepad_leftstick",
    DeviceId = "gamepad_1",
    InputType = InputType.Axis,
    InputCode = 0, // Left stick Y axis;
    DisplayName = "Left Stick",
    TriggerOn = TriggerCondition.Hold,
    Sensitivity = 1.5f;
};

await controlScheme.AddBindingAsync("Move", gamepadBinding);

// Yeni profil oluşturma;
var customProfile = await controlScheme.CreateProfileAsync("MyProfile", ProfileType.Custom);

// Profil uygulama;
await controlScheme.ApplyProfileAsync("MyProfile");

// Giriş işleme;
while (true)
{
    var inputEvents = GetInputEvents();
    foreach (var inputEvent in inputEvents)
    {
        var result = await controlScheme.ProcessInputAsync(inputEvent);
        if (result.IsProcessed)
        {
            // Eylemi işle;
            HandleAction(result.ActionName, result.Event.Value);
        }
    }

    await controlScheme.UpdateAsync(GetDeltaTime());
}

// İstatistikleri alma;
var stats = controlScheme.GetStatistics();
Console.WriteLine($"Total Actions: {stats.TotalActions}");
Console.WriteLine($"Total Bindings: {stats.TotalBindings}");
Console.WriteLine($"Events Per Second: {stats.InputStatistics.EventsPerSecond:F2}");
