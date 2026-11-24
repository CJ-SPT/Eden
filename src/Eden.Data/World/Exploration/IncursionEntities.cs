namespace Eden.Data.World.Exploration;

public sealed class SavedIncursionCampaign
{
    public long TaleId { get; set; }
    public int Clears { get; set; }
    public bool HeadquartersCleared { get; set; }
    public long? DefeatedAt { get; set; }
    public long LastUpdated { get; set; }
}

public sealed class SavedIncursionContribution
{
    public long SiteId { get; set; }
    public long PilotId { get; set; }
    public long TaleId { get; set; }
    public double Damage { get; set; }
    public double Repair { get; set; }
    public long PendingLoyalty { get; set; }
}

public sealed class SavedIncursionRepairDamage
{
    public long SiteId { get; set; }
    public long ShipId { get; set; }
    public long TaleId { get; set; }
    public int SystemId { get; set; }
    public double Shield { get; set; }
    public double Armor { get; set; }
    public double Hull { get; set; }
}
