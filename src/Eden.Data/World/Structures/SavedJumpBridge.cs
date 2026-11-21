namespace Eden.Data.World.Structures;

public sealed class SavedJumpBridge
{
    public long StructureId { get; set; }
    public int DestinationSystemId { get; set; }
    public double Capacitor { get; set; }
    public long ChargedAt { get; set; }
}
