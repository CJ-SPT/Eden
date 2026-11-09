using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Mining;

public sealed record MiningCrystalModifier(int EffectId, DogmaAttributeId Attribute, DogmaAttributeId Source, DogmaOperation Operation);

public enum MiningCrystalEffect
{
    Capacitor = 804,
    YieldAndResidue = 1200,
    Duration = 8206,
}

public static class MiningCrystalRules
{
    public const int Group = 482;
    public const int MercoxitGroup = 663;

    public static bool Compatible(IReadOnlyDictionary<DogmaAttributeId, double> module, int group) =>
        group is Group or MercoxitGroup
        && (
            module.GetValueOrDefault(DogmaAttributeId.ChargeGroup1) == group
            || module.GetValueOrDefault(DogmaAttributeId.ChargeGroup2) == group
        );

    // Build 3662168 fitted crystal programs modify their containing miner (otherID).
    public static IReadOnlyList<MiningCrystalModifier> Modifiers { get; } =
    [
        new(
            (int)MiningCrystalEffect.Capacitor,
            DogmaAttributeId.CapacitorNeed,
            DogmaAttributeId.CapacitorNeedBonus,
            DogmaOperation.PostPercent
        ),
        new(
            (int)MiningCrystalEffect.YieldAndResidue,
            DogmaAttributeId.MiningAmount,
            DogmaAttributeId.MiningCrystalYieldMultiplier,
            DogmaOperation.PreMultiply
        ),
        new(
            (int)MiningCrystalEffect.YieldAndResidue,
            DogmaAttributeId.MiningResidueMultiplier,
            DogmaAttributeId.MiningCrystalResidueMultiplierBonus,
            DogmaOperation.Add
        ),
        new(
            (int)MiningCrystalEffect.YieldAndResidue,
            DogmaAttributeId.MiningResidueChance,
            DogmaAttributeId.MiningCrystalResidueChanceBonus,
            DogmaOperation.Add
        ),
        new(
            (int)MiningCrystalEffect.Duration,
            DogmaAttributeId.Duration,
            DogmaAttributeId.MiningCrystalDurationMultiplier,
            DogmaOperation.PostMultiply
        ),
    ];
}
