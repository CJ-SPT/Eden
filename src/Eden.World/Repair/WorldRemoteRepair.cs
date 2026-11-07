namespace Eden.World.Repair;

public sealed class WorldRemoteRepair
{
    public long ModuleId { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long TargetId { get; set; }
    public int SystemId { get; set; }
    public string CycleJson { get; set; } = string.Empty;
}
