using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaEnlistment
{
    public long PilotId { get; set; }
    public MilitiaFaction? DirectFaction { get; set; }
    public long DirectCooldownUntil { get; set; }
}
