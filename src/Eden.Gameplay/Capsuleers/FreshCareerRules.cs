using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Capsuleers;

public static class FreshCareerRules
{
    public const int DefaultFounderCohorts = 1;
    public const int MaximumFounderCohorts = 5;
    public const int SlotsPerCohort = 2;
    public const int CourierSlot = 0;
    public const int CombatSlot = 1;
    public const int DeliveryJumps = 1;
    public const int DistributionLevel = 1;
    public const int MaximumFrontierJumps = 3;
    public const int NullSecurityReserveHulls = 2;

    public static int FounderSlots(CapsuleerPopulationPolicy policy) => policy.FreshFounderCohortsPerSystem * SlotsPerCohort;

    public static bool IsCombatSlot(int slot) => slot % SlotsPerCohort == CombatSlot;

    public static CapsuleerProfession Profession(int slot) => IsCombatSlot(slot) ? CapsuleerProfession.Combat : CapsuleerProfession.Hauling;

    public static bool AllowsMiningFounder(int slot, CapsuleerPopulationPolicy policy) =>
        policy.Enabled && policy.Miners && slot >= 0 && slot < FounderSlots(policy);

    public static bool AllowsFounder(int slot, CapsuleerPopulationPolicy policy) =>
        policy.Enabled
        && slot >= 0
        && slot < FounderSlots(policy)
        && (IsCombatSlot(slot) ? policy.FreshBountyPilots && policy.BeltRatting : policy.FreshCourierPilots);
}
