using System.Text.Json.Serialization;
using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Capsuleers;

[JsonConverter(typeof(JsonStringEnumConverter<CapsuleerRegionAccess>))]
public enum CapsuleerRegionAccess
{
    WorkAndTransit,
    TransitOnly,
}

public static class CapsuleerPopulationRules
{
    public const int MaximumTrafficMultiplier = 10;
    public const int MaximumGateVisitors = 16;
    public const int MaximumActivePilotCap = 1_000_000;
    public const int MaximumHomeRosterSlots = TrafficJourneyRules.HomeRosterSlots * MaximumTrafficMultiplier;
    private const int FractionPrecision = 1_000_000;

    public static void Validate(CapsuleerPopulationPolicy policy)
    {
        policy.Freight.Validate();
        if (policy.RatDamageReductionPercent is < 0 or > 100)
        {
            throw new InvalidDataException("Bot rat damage reduction must be between 0 and 100 percent.");
        }
        if (policy.ActivePilotCap is < 0 or > MaximumActivePilotCap)
        {
            throw new InvalidDataException("The active bot cap must be between 0 (unlimited) and 1,000,000.");
        }
        if (
            !Enum.IsDefined(policy.DefaultRegionAccess)
            || policy.RegionAccess is null
            || policy.RegionAccess.Values.Any(value => !Enum.IsDefined(value))
        )
        {
            throw new InvalidDataException("Region access must be Work and transit or Transit only.");
        }
        if (policy.SystemAccess is null || policy.SystemAccess.Values.Any(value => !Enum.IsDefined(value)))
        {
            throw new InvalidDataException("System access must be Work and transit or Transit only.");
        }
        if (policy.RegionalOperationsPerGroup is < 1 or > 3)
        {
            throw new InvalidDataException("Regional groups may run between one and three simultaneous operations.");
        }
        if (policy.RoamingGangSize is < RoamingGangRules.MinimumMembers or > RoamingGangRules.MaximumMembers)
        {
            throw new InvalidDataException("Roaming gangs must have between three and six members.");
        }
        if (policy.InterdictorPercent is < 0 or > 100 || policy.CampPercent is < 0 or > 100 || policy.CampSeconds is < 30 or > 1800)
        {
            throw new InvalidDataException("Camp chance must be 0 to 100 percent and shifts must be 30 to 1800 seconds.");
        }
        if (policy.RoamPercent is < 0 or > 100 || policy.GeneratedCombatPercent is < 0 or > 100)
        {
            throw new InvalidDataException("Patrol percentages must be between 0 and 100.");
        }
        if (policy.TrafficMultiplier is < 0 or > MaximumTrafficMultiplier || policy.GateVisitors is < 0 or > MaximumGateVisitors)
        {
            throw new InvalidDataException("Simulated-pilot traffic density must be between 0 and 10, and gate visitors between 0 and 16.");
        }
    }

    public static CapsuleerRegionAccess WorkAccess(CapsuleerPopulationPolicy policy, int systemId, int regionId) =>
        policy.SystemAccess.TryGetValue(systemId, out var access)
            ? access
            : policy.RegionAccess.GetValueOrDefault(regionId, policy.DefaultRegionAccess);

    public static bool AllowsWork(CapsuleerPopulationPolicy policy, int systemId, int regionId) =>
        WorkAccess(policy, systemId, regionId) == CapsuleerRegionAccess.WorkAndTransit;

    public static string Describe(CapsuleerRegionAccess access) =>
        access == CapsuleerRegionAccess.WorkAndTransit ? "Work and transit" : "Transit only";

    public static int AvailableSlots(int activePilotCap, int admitted) =>
        activePilotCap == 0 ? int.MaxValue : Math.Max(0, activePilotCap - admitted);

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
