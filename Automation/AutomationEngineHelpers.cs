using NEDA.EngineIntegration;

namespace Neda.Automation
{
    internal static class AutomationEngineHelpers
    {

        private static async Task NewMethodAsync(string projectName, UnrealEngineController ueController1) => await ueController1.BuildAndRunProjectAsync(projectName);
    }
}