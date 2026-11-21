using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaObjective
{
    public long Id { get; set; }
    public int SystemId { get; set; }
    public MilitiaObjectiveKind Kind { get; set; }
    public MilitiaFaction Faction { get; set; }
    public long? PilotId { get; set; }
    public long? TargetId { get; set; }
    public int Generation { get; set; }
    public long ReadyAt { get; set; }
    public long ExpiresAt { get; set; }
    public long? ReplaceAt { get; set; }
    public long Elapsed { get; set; }
    public int Waves { get; set; }
}
