using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaHub
{
    public int SystemId { get; set; }
    public long NpcId { get; set; }
    public MilitiaFaction? Attacker { get; set; }
    public long? TransferAt { get; set; }
}

public sealed class SavedMilitiaHubContribution
{
    public long NpcId { get; set; }
    public long PilotId { get; set; }
    public double Damage { get; set; }
}
