namespace Eden.Data.World.Capsuleers;

public sealed class SavedTrafficCorrection
{
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public long TemplateKillId { get; set; }
    public int OriginalHullTypeId { get; set; }
}
