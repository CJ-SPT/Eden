using Eden.Gameplay.Capsuleers;

namespace Eden.Data.World.Capsuleers;

public sealed class SavedRegionalConflict
{
    public int RegionId { get; set; }
    public RegionalConflictRegion State { get; set; } = new(0, 0);
}
