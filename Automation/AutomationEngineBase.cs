namespace Neda.Automation
{
    public class AutomationEngineBase
    {

        private async Task<avoid> NewMethodAsync(string projectName) => await ueController.BuildAndRunProjectAsync(projectName).ConfigureAwait(false);
    }
}