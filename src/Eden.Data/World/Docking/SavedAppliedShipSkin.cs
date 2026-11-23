namespace Eden.Data.World.Docking;

public sealed class SavedAppliedShipSkin
{
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public int SkinId { get; set; }
    public long? Expires { get; set; }
}
