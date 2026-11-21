namespace Eden.Data.World.Flight;

public sealed class SavedJumpTimers
{
    public long PilotId { get; set; }
    public long ActivationUntil { get; set; }
    public long FatigueUntil { get; set; }
    public long UpdatedAt { get; set; }
}

public sealed class SavedCynosuralField
{
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public long ModuleId { get; set; }
    public long BeaconId { get; set; }
    public long FleetId { get; set; }
    public int SystemId { get; set; }
    public long EndsAt { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public float Radius { get; set; }
    public double SpawnRadius { get; set; }
}
