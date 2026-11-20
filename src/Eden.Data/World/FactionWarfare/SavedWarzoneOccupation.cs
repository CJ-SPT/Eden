using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedWarzoneOccupation
{
    public int SystemId { get; set; }
    public MilitiaFaction Occupier { get; set; }
    public int VictoryPoints { get; set; }
    public long? CapturedAt { get; set; }
}
