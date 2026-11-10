using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerPopulationRules
{
    public const int MaximumTrafficMultiplier = 10;
    public const int MaximumGateVisitors = 16;
    public const int MaximumHomeRosterSlots = TrafficJourneyRules.HomeRosterSlots * MaximumTrafficMultiplier;
    private const int FractionPrecision = 1_000_000;

    public static void Validate(CapsuleerPopulationPolicy policy)
    {
        if (policy.TrafficMultiplier is < 0 or > MaximumTrafficMultiplier || policy.GateVisitors is < 0 or > MaximumGateVisitors)
        {
            throw new InvalidDataException("Simulated-pilot traffic density must be between 0 and 10, and gate visitors between 0 and 16.");
        }
    }

    public static bool Included(CapsuleerCareer career, int slot, int trafficCount, CapsuleerPopulationPolicy policy) =>
        policy.Enabled
        && career switch
        {
            CapsuleerCareer.Mining => policy.Miners,
            CapsuleerCareer.Hauling => slot < MaximumHomeRosterSlots
                ? slot < trafficCount
                : slot / TrafficJourneyRules.VisitorSlotStride < policy.GateVisitors,
            _ => false,
        };

    public static int Traffic(int baseline, long seed, int system, CapsuleerPopulationPolicy policy)
    {
        Validate(policy);
        ArgumentOutOfRangeException.ThrowIfNegative(baseline);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(baseline, TrafficJourneyRules.HomeRosterSlots);
        if (!policy.Enabled)
        {
            return 0;
        }
        decimal scaled = baseline * policy.TrafficMultiplier;
        int count = (int)decimal.Floor(scaled);
        // Stable fractional rounding keeps a reduced density spread across systems, including one-pilot branches.
        decimal fraction = scaled - count;
        ulong draw = CapsuleerProvisioningRules.Seed(seed, system, CapsuleerCareer.Hauling) % FractionPrecision;
        return count + (draw < fraction * FractionPrecision ? 1 : 0);
    }
}
