namespace Eden.Data.World.Structures;

public sealed class SavedPilotTether
{
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public long StructureId { get; set; }
    public long EngagedAt { get; set; }
    public long RepairAt { get; set; }
    public bool RepairCompleted { get; set; }
}
