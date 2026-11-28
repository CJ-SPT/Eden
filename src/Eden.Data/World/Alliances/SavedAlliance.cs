namespace Eden.Data.World.Alliances;

public sealed class SavedAlliance
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Ticker { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public long CreatorId { get; set; }
    public int CreatorCorporationId { get; set; }
    public int? ExecutorCorporationId { get; set; }
    public int? LastExecutorCorporationId { get; set; }
    public bool? AutomaticMaintenancePayment { get; set; }
    public long CreatedAt { get; set; }
    public long? ClosedAt { get; set; }
}

public sealed class SavedAllianceMember
{
    public int CorporationId { get; set; }
    public int AllianceId { get; set; }
    public long JoinedAt { get; set; }
    public int? ChosenExecutorId { get; set; }
}
