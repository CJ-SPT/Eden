namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyCampaignCredit
{
    public long HubId { get; set; }
    public int AllianceId { get; set; }
    public int SystemId { get; set; }
    public long? VictoryAt { get; set; }
}
