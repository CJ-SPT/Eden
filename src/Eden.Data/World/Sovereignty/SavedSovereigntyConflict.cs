using Eden.Gameplay.Sovereignty;

namespace Eden.Data.World.Sovereignty;

public sealed class SavedSovereigntyConflict
{
    public int SystemId { get; set; }
    public long HubId { get; set; }
    public int ConstellationId { get; set; }
    public int DefenderAllianceId { get; set; }
    public SovereigntyPhase Phase { get; set; }
    public long WindowStart { get; set; }
    public long WindowEnd { get; set; }
    public long? CampaignAt { get; set; }
    public int DefenderPoints { get; set; }
    public long SecureUntil { get; set; }
    public int Sequence { get; set; }
    public long NextSpawnAt { get; set; }
    public double Value { get; set; }
    public long UpdatedAt { get; set; }
    public int? ScoringTeam { get; set; }
    public double Rate { get; set; }
    public bool Blocked { get; set; }
}
