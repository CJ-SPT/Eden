namespace Eden.Gameplay.FactionWarfare;

public enum MilitiaLoyaltySource
{
    Complex,
    Battlefield,
    InfrastructureHub,
    AdvantageObjective,
    Mission,
    PlayerShipLoss,
}

public enum MilitiaStatisticOwner
{
    Pilot,
    Corporation,
    Alliance,
    Faction,
}

public readonly record struct MilitiaActivity(long Kills, long VictoryPoints)
{
    public static MilitiaActivity operator +(MilitiaActivity first, MilitiaActivity second) =>
        new(checked(first.Kills + second.Kills), checked(first.VictoryPoints + second.VictoryPoints));
}

public readonly record struct MilitiaStatistics(MilitiaActivity Yesterday, MilitiaActivity LastWeek, MilitiaActivity Total)
{
    public static MilitiaStatistics operator +(MilitiaStatistics first, MilitiaStatistics second) =>
        new(first.Yesterday + second.Yesterday, first.LastWeek + second.LastWeek, first.Total + second.Total);
}

public sealed record MilitiaLeaderboard(MilitiaStatisticOwner Owner, MilitiaStatistics Top, MilitiaStatistics All);

public sealed record MilitiaRank(long CharacterId, MilitiaFaction Faction, int Rank);

public sealed record MilitiaFactionStatistics(
    MilitiaFaction Faction,
    int SystemsCount,
    int TotalMembersCount,
    int MilitiaMembersCount,
    int CorporationsCount,
    int TopCorporation,
    int TopMemberCount,
    MilitiaStatistics Activity
);

public interface IMilitiaProgression
{
    MilitiaRank? Rank(long pilotId);
    IReadOnlyList<MilitiaRank> Ranks(long pilotId);
    MilitiaStatistics Statistics(long pilotId, MilitiaStatisticOwner owner);
    IReadOnlyList<MilitiaLeaderboard> Leaderboards();
    IReadOnlyList<MilitiaFactionStatistics> Factions();
    int CorporationPilots(long pilotId);
}

public static class MilitiaProgressionRules
{
    // Eden's base fraction. CCP publishes the location multipliers, not the underlying amount.
    public const double FriendlyFireBaseFactionLoss = .025;
    public const double InsideSiteFriendlyFireMultiplier = 2;
    public const double OutsideSiteFriendlyFireMultiplier = .5;

    public const int MaximumRank = 9;
    public const int MaximumPvpLoyalty = 50_000;
    public const decimal IskPerPvpLoyaltyPoint = 10_000m;
    public const double PromotionFactionFraction = 0.02;
    public const double CaptureStandingFraction = 0.005;
    public const double MissionFailureFraction = 0.02;

    public static int Rank(double standing) => Math.Clamp((int)standing, 0, MaximumRank);

    public static long PvpLoyalty(decimal hull, decimal insurance, decimal fittingsAndCargo) =>
        (long)Math.Clamp((hull - insurance + fittingsAndCargo) / IskPerPvpLoyaltyPoint, 0, MaximumPvpLoyalty);

    public static long Share(long total, int index, int recipients) => total / recipients + (index < total % recipients ? 1 : 0);
}
