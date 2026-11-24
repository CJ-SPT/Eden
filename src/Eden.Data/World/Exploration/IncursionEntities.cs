namespace Eden.Data.World.Exploration;

public sealed class SavedIncursionCampaign
{
    public long TaleId { get; set; }
    public int Clears { get; set; }
    public long LastUpdated { get; set; }
}

public sealed class SavedIncursionContribution
{
    public long SiteId { get; set; }
    public long PilotId { get; set; }
    public long TaleId { get; set; }
    public double Damage { get; set; }
    public long PendingLoyalty { get; set; }
}
