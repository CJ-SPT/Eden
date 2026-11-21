namespace Eden.Gameplay.FactionWarfare;

public sealed record MilitiaAffiliation(MilitiaFaction? Direct, MilitiaFaction? Corporation, long DirectCooldownUntil)
{
    public MilitiaFaction? Effective => Corporation ?? Direct;
}

public interface IMilitiaEnlistment
{
    MilitiaAffiliation Read(long pilotId);
    MilitiaFaction? EffectiveFaction(long pilotId);
    IReadOnlyList<MilitiaFaction> AllowedFactions(int corporationId);
    IReadOnlyList<MilitiaFaction> SetAllowedFactions(long pilotId, IReadOnlyList<MilitiaFaction> factions);
    void EnlistDirectly(long pilotId, MilitiaFaction faction);
    void JoinNpcMilitia(long pilotId, MilitiaFaction faction);
    void RetireDirectly(long pilotId);
}

public static class MilitiaRules
{
    // 3662168 militia corporations and paired empire combat enemies.
    public const int StateProtectorate = 1000180;
    public const int TribalLiberationForce = 1000182;
    public const int ImperialCrusade = 1000179;
    public const int FederalDefenceUnion = 1000181;
    public static readonly TimeSpan DirectCooldown = TimeSpan.FromHours(24);

    public static int Corporation(MilitiaFaction faction) =>
        faction switch
        {
            MilitiaFaction.Caldari => StateProtectorate,
            MilitiaFaction.Minmatar => TribalLiberationForce,
            MilitiaFaction.Amarr => ImperialCrusade,
            MilitiaFaction.Gallente => FederalDefenceUnion,
            _ => throw new InvalidDataException("Choose one of the four empire militias."),
        };

    public static MilitiaFaction? CorporationFaction(int corporationId) =>
        corporationId switch
        {
            StateProtectorate => MilitiaFaction.Caldari,
            TribalLiberationForce => MilitiaFaction.Minmatar,
            ImperialCrusade => MilitiaFaction.Amarr,
            FederalDefenceUnion => MilitiaFaction.Gallente,
            _ => null,
        };

    public static MilitiaFaction Opponent(MilitiaFaction faction) =>
        faction switch
        {
            MilitiaFaction.Amarr => MilitiaFaction.Minmatar,
            MilitiaFaction.Minmatar => MilitiaFaction.Amarr,
            MilitiaFaction.Caldari => MilitiaFaction.Gallente,
            MilitiaFaction.Gallente => MilitiaFaction.Caldari,
            _ => throw new ArgumentOutOfRangeException(nameof(faction)),
        };

    public static bool AreEnemies(MilitiaFaction? first, MilitiaFaction? second) =>
        (first, second)
            is
                (MilitiaFaction.Amarr, MilitiaFaction.Minmatar)
                or
                (MilitiaFaction.Minmatar, MilitiaFaction.Amarr)
                or
                (MilitiaFaction.Caldari, MilitiaFaction.Gallente)
                or
                (MilitiaFaction.Gallente, MilitiaFaction.Caldari);

    public static bool CanUseDocking(WarzoneSystem? system, MilitiaFaction? faction, bool capsule) =>
        capsule || system is null || system.Adjacency == WarzoneAdjacency.Frontline || !AreEnemies(faction, system.Occupier);

    public static bool IsThirdPartyCombat(MilitiaFaction? source, MilitiaFaction? recipient, bool recipientInCombat) =>
        recipientInCombat && recipient is not null && source != recipient;
}
