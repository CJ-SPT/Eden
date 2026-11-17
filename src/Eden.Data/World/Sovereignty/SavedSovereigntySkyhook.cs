namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntySkyhook
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public long PlanetId { get; set; }
    public int CorporationId { get; set; }
    public bool Active { get; set; }
    public long OnlineAt { get; set; }
    public long UpdatedAt { get; set; }
    public long Secured { get; set; }
    public long Surplus { get; set; }
}
