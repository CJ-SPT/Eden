using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Capsuleers;

public static class FreshCareerRules
{
    public const int CourierSlot = 0;
    public const int CombatSlot = 1;
    public const int DeliveryJumps = 1;
    public const int DistributionLevel = 1;
    public const int MaximumFrontierJumps = 3;
    public const int NullSecurityReserveHulls = 2;

    public static CapsuleerProfession Profession(int slot) => slot == CombatSlot ? CapsuleerProfession.Combat : CapsuleerProfession.Hauling;

    public static bool AllowsFounder(int slot, CapsuleerPopulationPolicy policy) =>
        policy.Enabled
        && slot switch
        {
            CourierSlot => policy.FreshCourierPilots,
            CombatSlot => policy.FreshBountyPilots && policy.BeltRatting,
            _ => false,
        };
}
