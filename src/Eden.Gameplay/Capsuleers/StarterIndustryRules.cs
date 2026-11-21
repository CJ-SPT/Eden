namespace Eden.Gameplay.Capsuleers;

public static class StarterIndustryRules
{
    // The pinned catalog's Miner I original and two complementary opening goals.
    public const int MinerBlueprint = 785;
    public const int VentureBlueprint = 32881;
    public const int ProducerSlot = 0;
    public const int BuyerSlot = 1;
    public const int CrewSize = FreshCareerRules.SlotsPerCohort;
    public const int OrderDays = 90;

    public static bool IsProducerSlot(int slot) => slot % CrewSize == ProducerSlot;

    public static bool IsBuyerSlot(int slot) => slot % CrewSize == BuyerSlot;
}
