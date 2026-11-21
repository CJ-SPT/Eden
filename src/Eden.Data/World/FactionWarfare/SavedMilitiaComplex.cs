using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaComplex
{
    public long Slot { get; set; }
    public int Generation { get; set; }
    public MilitiaFaction Occupier { get; set; }
    public long Progress { get; set; }
    public long? ReplaceAt { get; set; }
    public long? DefenderId { get; set; }
}
