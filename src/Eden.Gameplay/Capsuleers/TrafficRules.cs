using Eden.Gameplay.Combat;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Capsuleers;

public enum TrafficSecurityBand
{
    Null = 0,
    Low = 1,
    High = 2,
}

public static class TrafficRules
{
    public const int SnapshotVersion = 1;

    public static bool LightHull(HullClass hull) => hull is HullClass.Corvette or HullClass.Shuttle;

    public const int MaximumSkillLevel = 5;

    public static TrafficSecurityBand SecurityBand(double security) =>
        NpcSpawnRules.SecurityBand(security) switch
        {
            NpcSecurityBand.High or NpcSecurityBand.Safest => TrafficSecurityBand.High,
            NpcSecurityBand.Low => TrafficSecurityBand.Low,
            _ => TrafficSecurityBand.Null,
        };

    public static bool RouteAllows(HullClass hull, bool visitsHighSecurity) =>
        hull is not (HullClass.Capsule or HullClass.Supercarrier or HullClass.Titan or HullClass.Unclassified)
        && (
            !visitsHighSecurity
            || hull
                is not (
                    HullClass.Carrier
                    or HullClass.Dreadnought
                    or HullClass.ForceAuxiliary
                    or HullClass.CapitalIndustrial
                    or HullClass.LancerDreadnought
                    or HullClass.CommandCarrier
                )
        );

    public static int Start(long seed, int system, int slot, int count) =>
        count == 0 ? 0 : (int)(CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling, slot) % (ulong)count);
}
