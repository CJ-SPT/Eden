using Eden.Gameplay.Flight;

namespace Eden.Data.World.Flight;

public sealed class SavedJumpPortal
{
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public long ModuleId { get; set; }
    public long FleetId { get; set; }
    public int Origin { get; set; }
    public int Destination { get; set; }
    public CapitalJumpTarget Target { get; set; }
    public long EmitterId { get; set; }
    public long BeaconId { get; set; }
    public long StartedAt { get; set; }
    public long EndsAt { get; set; }
}
