namespace Eden.Data.World.Exploration;

public sealed class SavedDedGateUnlock
{
    public long SiteId { get; set; }
    public long GateObjectId { get; set; }
    public long PilotId { get; set; }
    public long ResetAt { get; set; }
}
