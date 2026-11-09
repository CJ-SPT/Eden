using Eden.Gameplay.Mining;

namespace Eden.Data.World.Mining;

public sealed class SavedMiningSite
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public MiningFamily Family { get; set; }
    public int DungeonId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long ExpiresAt { get; set; }
    public long? ReplaceAt { get; set; }
}

public sealed class SavedMiningDeposit
{
    public long Id { get; set; }
    public long SiteId { get; set; }
}
