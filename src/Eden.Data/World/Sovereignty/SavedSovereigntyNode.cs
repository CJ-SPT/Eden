namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyNode
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public int SourceSystemId { get; set; }
    public long HubId { get; set; }
    public int CorporationId { get; set; }
    public int AllianceId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Value { get; set; }
    public long UpdatedAt { get; set; }
    public int? ScoringTeam { get; set; }
    public double Rate { get; set; }
    public bool Blocked { get; set; }
}
