namespace Eden.Data.World.Docking;

public sealed class SavedShipSkinLicense
{
    public long PilotId { get; set; }
    public int SkinId { get; set; }
    public long? Expires { get; set; }
    public bool IsSingleUse { get; set; }
}
