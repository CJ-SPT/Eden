namespace Eden.Data.World.Market;

public sealed class SavedMarketUnlock
{
    public int TypeId { get; set; }
    public long AccountId { get; set; }
    public long PilotId { get; set; }
    public long JobId { get; set; }
    public long ClaimedAt { get; set; }
}
