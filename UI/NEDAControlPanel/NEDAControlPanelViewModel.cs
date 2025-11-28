using System.Collections.ObjectModel;

namespace NEDA.EngineIntegration.Controllers.NEDAControlPanel
{
    public class NEDAControlPanelViewModel
    {
        public ObservableCollection<NPC> NPCs { get; set; } = new ObservableCollection<NPC>();
        private UnrealEngineController _ueController;

        public NEDAControlPanelViewModel(UnrealEngineController ueController)
        {
            _ueController = ueController;
        }

        public async void AddNPC(string name, NPCType type)
        {
            var pos = _ueController.GetRandomPosition("NEDA_TestGame");
            await _ueController.PlaceNPCAsync(name, type, pos);
            NPCs.Add(new NPC { Name = name, Type = type, Position = pos });
        }

        public async void UpdateNPCs((float x, float y, float z) playerPos)
        {
            await _ueController.UpdateNPCsAsync(playerPos);
        }
    }
}
