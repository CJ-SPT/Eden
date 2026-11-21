using Eden.Gameplay.FactionWarfare;

namespace Eden.Data.World.FactionWarfare;

public sealed class SavedMilitiaOrganization
{
    public int OwnerId { get; set; }
    public MilitiaOrganizationKind Kind { get; set; }
    public MilitiaFaction Faction { get; set; }
    public MilitiaOrganizationPhase Phase { get; set; }
    public long EffectiveAt { get; set; }
    public long? StartedAt { get; set; }
    public long ReviewedAt { get; set; }
    public long? StandingExpiry { get; set; }
}

public sealed class SavedMilitiaStandingWarning
{
    public long PilotId { get; set; }
    public int OwnerId { get; set; }
    public int CorporationId { get; set; }
    public MilitiaFaction Faction { get; set; }
    public long ExpiresAt { get; set; }
}
