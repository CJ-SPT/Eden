namespace Eden.Data.World.Alliances;

public sealed class SavedAllianceBill
{
    public long Id { get; set; }
    public int AllianceId { get; set; }
    public long IssuedAt { get; set; }
    public long DueAt { get; set; }
    public long AmountCents { get; set; }
    public int CorporationCount { get; set; }
    public long? PaidAt { get; set; }
    public int? PaidByCorporationId { get; set; }
    public int DivisionKey { get; set; }
    public bool Renewed { get; set; }
}

public sealed class SavedAllianceLifecycle
{
    public int AllianceId { get; set; }
    public long? ClosureAt { get; set; }
}
