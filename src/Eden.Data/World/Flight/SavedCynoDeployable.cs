namespace Eden.Data.World.Flight;

public sealed class SavedCynoDeployable
{
    public long Id { get; set; }
    public long BeaconId { get; set; }
    public long PilotId { get; set; }
    public long FleetId { get; set; }
    public int SystemId { get; set; }
    public int TypeId { get; set; }
    public long OnlineAt { get; set; }
    public long ExpiresAt { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double? Shield { get; set; }
    public double? Armor { get; set; }
    public double? Structure { get; set; }
    public long DamagedAt { get; set; }
}
