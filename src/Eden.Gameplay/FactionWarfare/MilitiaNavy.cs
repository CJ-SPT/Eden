using Eden.Gameplay.Combat;

namespace Eden.Gameplay.FactionWarfare;

public sealed record MilitiaNavyUpdate(MilitiaFaction? WarningFaction = null, bool SceneChanged = false);

public interface IMilitiaNavy
{
    MilitiaNavyUpdate Advance(long pilotId, bool canEngage, long now);
    void Leave(long pilotId, long now);
    void Sweep(long now);
}

public static class MilitiaNavyRules
{
    public const double FormationSpacing = 1200;
    public const double ClosePursuitRange = NpcEncounterDefinitions.ChaseLeash;

    // Eden timing for catalog-backed wings. A change of grid or a defeated wing gives the pilot time to leave.
    public static readonly TimeSpan RedeployDelay = TimeSpan.FromSeconds(10);

    public static TimeSpan ResponseDelay(double security) =>
        TimeSpan.FromSeconds(
            security >= .85 ? 6
            : security >= .65 ? 9
            : 12
        );

    public static bool Responds(MilitiaFaction? pilotFaction, MilitiaFaction? controllingFaction, double security, bool capsule) =>
        !capsule && security >= CrimewatchRules.HighSecurity && MilitiaRules.AreEnemies(pilotFaction, controllingFaction);
}
