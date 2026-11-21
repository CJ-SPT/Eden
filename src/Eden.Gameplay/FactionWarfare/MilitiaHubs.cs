using Eden.Gameplay.Combat;

namespace Eden.Gameplay.FactionWarfare;

public enum MilitiaHubPhase
{
    Stable,
    Contested,
    Vulnerable,
    Captured,
}

public interface IMilitiaHubs
{
    void Sweep(long now);
}

public static class MilitiaHubRules
{
    public const int GroupId = 925;
    public const int BaseLoyaltyPoints = 40_000;
    public const int DowntimeHourUtc = 11;
    public const double CaptureStructureFraction = .01;

    public static MilitiaHubPhase Phase(int score, long? transferAt) =>
        transferAt.HasValue ? MilitiaHubPhase.Captured
        : score >= FactionWarzoneRules.VictoryPointThreshold ? MilitiaHubPhase.Vulnerable
        : score > 0 ? MilitiaHubPhase.Contested
        : MilitiaHubPhase.Stable;

    public static bool CanAttack(MilitiaHubPhase phase, MilitiaFaction occupier, MilitiaFaction? faction) =>
        phase == MilitiaHubPhase.Vulnerable && MilitiaRules.AreEnemies(occupier, faction);

    public static bool Captures(HullDefense defense, HullDamage remaining) =>
        remaining.Structure <= defense.Structure * CaptureStructureFraction;

    public static long NextTransfer(long now)
    {
        DateTime utc = DateTime.FromFileTimeUtc(now);
        var downtime = utc.Date.AddHours(DowntimeHourUtc);
        return (utc < downtime ? downtime : downtime.AddDays(1)).ToFileTimeUtc();
    }

    public static int TypeId(MilitiaFaction faction) =>
        faction switch
        {
            MilitiaFaction.Amarr => 29286,
            MilitiaFaction.Caldari => 29302,
            MilitiaFaction.Gallente => 29303,
            MilitiaFaction.Minmatar => 29304,
            _ => throw new ArgumentOutOfRangeException(nameof(faction)),
        };

    // Remainder points go to the earliest pilot IDs, so the whole pool is conserved.
    public static long Share(int position, int count) => BaseLoyaltyPoints / count + (position < BaseLoyaltyPoints % count ? 1 : 0);
}
