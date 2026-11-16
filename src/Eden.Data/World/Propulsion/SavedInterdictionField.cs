using Eden.Gameplay.Propulsion;

namespace Eden.Data.World.Propulsion;

public sealed class SavedInterdictionField
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long SourceShipId { get; set; }
    public long ModuleId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public InterdictionKind Kind { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Radius { get; set; }
    public double HitPoints { get; set; }
    public long StartedAt { get; set; }
    public long ExpiresAt { get; set; }
}
