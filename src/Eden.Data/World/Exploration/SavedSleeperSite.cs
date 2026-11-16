namespace Eden.Data.World.Exploration;

public sealed class SavedSleeperSite
{
    public long Id { get; set; }
    public long? ParentSiteId { get; set; }
    public int SystemId { get; set; }
    public long? ActivatedAt { get; set; }
    public long? ExpiresAt { get; set; }
    public int HackStarts { get; set; }
    public int HackFailures { get; set; }
    public int HackResolutions { get; set; }
    public int AggressedWave { get; set; } = -1;
}
