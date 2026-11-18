using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

// Player hulls from the pinned build 3662168 catalog.
public enum CapsuleerMiningHull
{
    Venture = (int)TechOneHullType.Venture,
    Covetor = 17476,
    Retriever = 17478,
    Procurer = 17480,
    Hulk = 22544,
    Skiff = 22546,
    Mackinaw = 22548,
}

public static class CapsuleerMiningRules
{
    public const int MaximumCrew = 3;
    public const int BeltsForLargerCrew = 3;

    public static MiningLaserType Laser(CapsuleerMiningHull hull) =>
        hull
            is CapsuleerMiningHull.Covetor
                or CapsuleerMiningHull.Retriever
                or CapsuleerMiningHull.Procurer
                or CapsuleerMiningHull.Hulk
                or CapsuleerMiningHull.Skiff
                or CapsuleerMiningHull.Mackinaw
            ? MiningLaserType.StripMinerI
            : MiningLaserType.MinerI;

    public static CapsuleerMiningHull[] Roster(
        int belts,
        ulong seed,
        IReadOnlyList<CapsuleerMiningHull> barges,
        IReadOnlyList<CapsuleerMiningHull> exhumers
    )
    {
        if (barges.Count == 0)
        {
            return [CapsuleerMiningHull.Venture];
        }
        int barge = (int)(seed % (ulong)barges.Count);
        if (belts < BeltsForLargerCrew)
        {
            return [CapsuleerMiningHull.Venture, barges[barge]];
        }
        var third = exhumers.Count > 0 ? exhumers[(int)(seed % (ulong)exhumers.Count)] : barges[(barge + 1) % barges.Count];
        return [CapsuleerMiningHull.Venture, barges[barge], third];
    }

    public static bool WorkingField(MinerProgress work) =>
        work.Activity is MinerActivity.FlyingToBelt or MinerActivity.ApproachingOre or MinerActivity.Locking or MinerActivity.Mining;

    public static bool ClaimsRock(MinerProgress work) =>
        work.Activity is MinerActivity.ApproachingOre or MinerActivity.Locking or MinerActivity.Mining;
}
