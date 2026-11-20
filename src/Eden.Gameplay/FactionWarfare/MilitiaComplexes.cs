using Eden.Gameplay.Docking;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.FactionWarfare;

public enum MilitiaComplexFamily
{
    Scout,
    Small,
    Medium,
    Large,
    Open,
}

public sealed record MilitiaCaptureBracket(double Points, double Minutes, MilitiaFaction? Capturing, MilitiaFaction? Previous, bool Paused);

public interface IMilitiaComplexes
{
    bool IsWarzone(int systemId);
    IReadOnlyList<CombatAnomaly> Sites(int systemId, long now);
    IReadOnlyList<AnomalySystemSites> Directory(long now);
    void Advance(int systemId, IReadOnlyList<MilitiaComplexPilot> pilots, long now);
    SpacePosition Gate(long pilotId, long gateId, long now, SpacePosition? activate = null);
    SpacePosition Redirect(int systemId, SpacePosition position, long now);
}

public sealed record MilitiaComplexPilot(long PilotId, long ShipId, SpacePosition Position, long ConnectionId = 0);

public sealed record MilitiaComplexRewardPolicy(int Minutes, int BasePoints, int Capacity, int VictoryPoints)
{
    public long Duration => TimeSpan.FromMinutes(Minutes).Ticks;
}

public static class MilitiaComplexRules
{
    private const int VentureType = 32880;
    private const long SiteBase = 8_620_000_000_000;
    private const long ObjectBase = 8_640_000_000_000;
    private const long IdentityRange = 10_000_000_000;
    private const int SlotsPerGeneration = 100_000;
    public const int CapturePointGroup = 922;
    public const double CaptureRange = 30_000;
    public const double GateRange = 90_000;
    public static readonly TimeSpan MaximumSampleGap = TimeSpan.FromSeconds(3);

    public static long Slot(int systemId, MilitiaComplexFamily family) => checked((systemId - 30_000_000) * 10L + (int)family);

    public static long SiteId(long slot, int generation) => checked(SiteBase + generation * (long)SlotsPerGeneration + slot);

    public static long GateId(long siteId) => ObjectBase + (siteId - SiteBase);

    public static long SiteFromGate(long gateId) => SiteBase + (gateId - ObjectBase);

    public static long BeaconId(long siteId) => GateId(siteId) + IdentityRange;

    public static long PointId(long siteId) => BeaconId(siteId) + IdentityRange;

    public static bool IsSite(long id) => id >= SiteBase && id < SiteBase + IdentityRange;

    public static bool IsGate(long id) => id >= ObjectBase && id < ObjectBase + IdentityRange;

    public static bool CanCapture(int shipType, int group) => shipType != VentureType && group != FleetDefinitions.CapsuleGroup;

    public static TimeSpan Replacement(WarzoneAdjacency adjacency) =>
        TimeSpan.FromMinutes(
            adjacency switch
            {
                WarzoneAdjacency.Frontline => 20,
                WarzoneAdjacency.CommandOperations => 40,
                _ => 60,
            }
        );

    public static MilitiaComplexRewardPolicy Policy(MilitiaComplexFamily family, int capacity) =>
        family switch
        {
            MilitiaComplexFamily.Scout => new(10, 10_000, capacity, 100),
            MilitiaComplexFamily.Small => new(10, 15_000, capacity, 150),
            MilitiaComplexFamily.Medium => new(15, 20_000, capacity, 150),
            MilitiaComplexFamily.Large => new(15, 25_000, capacity, 250),
            MilitiaComplexFamily.Open => new(15, 30_000, capacity, 350),
            _ => throw new ArgumentOutOfRangeException(nameof(family)),
        };

    public static long Advance(long progress, int side, long elapsed, long duration) =>
        Math.Clamp(progress + side * elapsed, -duration, duration);

    public static int VictoryPoints(int score, bool offense, MilitiaComplexRewardPolicy policy) =>
        Math.Clamp(
            score + (offense ? policy.VictoryPoints : -policy.VictoryPoints),
            0,
            FactionWarzoneRules.VictoryPointThreshold + FactionWarzoneRules.VictoryPointBuffer
        );

    public static long PointsPerPilot(WarzoneSystem system, bool offense, MilitiaComplexRewardPolicy policy, int pilots)
    {
        if (pilots <= 0 || offense && system.VictoryPoints >= FactionWarzoneRules.VictoryPointThreshold)
        {
            return 0;
        }
        decimal adjacency = system.Adjacency switch
        {
            WarzoneAdjacency.Frontline => 1.5m,
            WarzoneAdjacency.CommandOperations => 1m,
            _ => 0.01m,
        };
        decimal defensiveShare = offense ? 1 : Math.Min(1, system.VictoryPoints / (decimal)FactionWarzoneRules.VictoryPointThreshold);
        return (long)(policy.BasePoints * adjacency * defensiveShare * Math.Min(policy.Capacity, pilots) / pilots);
    }

    public static long CorporationTax(long points, double rate) => (long)(points * (decimal)Math.Clamp(rate, 0, 1));
}
