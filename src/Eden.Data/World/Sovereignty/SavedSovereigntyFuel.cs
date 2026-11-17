namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyFuel
{
    public long Id { get; set; }
    public long HubId { get; set; }
    public int TypeId { get; set; }
    public long Amount { get; set; }
    public long Remainder { get; set; }
}
