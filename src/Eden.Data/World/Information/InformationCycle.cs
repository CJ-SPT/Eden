using Eden.Gameplay.Inventory;

namespace Eden.Data.World.Information;

public sealed class SavedInformationCycle
{
    public long ModuleId { get; set; }
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public long TargetId { get; set; }
    public DogmaEffect Effect { get; set; }
    public long StartedAt { get; set; }
    public long ReadyAt { get; set; }
    public string ModuleJson { get; set; } = "";
}
