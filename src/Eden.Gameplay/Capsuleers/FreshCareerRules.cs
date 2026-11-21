namespace Eden.Gameplay.Capsuleers;

public static class FreshCareerRules
{
    public const int CourierSlot = 0;
    public const int CombatSlot = 1;
    public const int DeliveryJumps = 1;
    public const int DistributionLevel = 1;

    public static CapsuleerProfession Profession(int slot) => slot == CombatSlot ? CapsuleerProfession.Combat : CapsuleerProfession.Hauling;
}
