namespace Eden.Data.World.Exploration;

public sealed class SavedWormholeConnection
{
    public long Id { get; set; }
    public long EndpointId { get; set; }
    public int SourceSystem { get; set; }
    public int DestinationSystem { get; set; }
    public int TypeId { get; set; }
    public int Generation { get; set; }
    public long ActivatedAt { get; set; }
    public long ExpiresAt { get; set; }
    public bool ExitVisible { get; set; }
    public double TotalMass { get; set; }
    public double RemainingMass { get; set; }
    public long MassUpdatedAt { get; set; }
    public long ReplaceAt { get; set; }
}
