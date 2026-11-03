using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Mining;

public sealed record MiningModuleInputs(
    int GroupId,
    int HullTypeId,
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes,
    IReadOnlyDictionary<DogmaAttributeId, double> HullAttributes,
    IReadOnlyDictionary<MiningSkill, int> Levels
);

public static class MiningRules
{
    public sealed record Collection(int Extracted, int CriticalBonus, int Residue, bool CargoFull);

    public static Collection Collect(
        long remainingUnits,
        double availableVolume,
        double oreVolume,
        MiningLaser laser,
        MiningCollection collection
    )
    {
        if (!(oreVolume > 0) || !double.IsFinite(oreVolume))
        {
            throw new InvalidDataException("Invalid ore volume.");
        }
        if (
            remainingUnits < 0
            || availableVolume < 0
            || !double.IsFinite(availableVolume)
            || !(laser.Volume > 0)
            || !double.IsFinite(laser.Volume)
            || laser.CriticalChance < 0
            || !double.IsFinite(laser.CriticalChance)
            || laser.CriticalBonusYield < 0
            || !double.IsFinite(laser.CriticalBonusYield)
            || !Probability(laser.ResidueChance)
            || laser.ResidueMultiplier < 0
            || !double.IsFinite(laser.ResidueMultiplier)
            || collection.Fraction < 0
            || collection.Fraction > 1
            || !double.IsFinite(collection.Fraction)
            || (collection.Completed && collection.Fraction != 1)
            || !Roll(collection.Rolls.Yield)
            || !Roll(collection.Rolls.Critical)
            || !Roll(collection.Rolls.CriticalYield)
            || !Roll(collection.Rolls.Residue)
            || !Roll(collection.Rolls.ResidueYield)
        )
        {
            throw new InvalidDataException("Invalid mining yield.");
        }
        int free = (int)Math.Min(int.MaxValue, Math.Floor((availableVolume + InventoryDefinitions.ResourceTolerance) / oreVolume));
        int planned = Math.Min(free, RoundedUnits(laser.Volume * collection.Fraction / oreVolume, collection.Rolls.Yield));
        // Residue is removed from the rock first; critical ore never contributes to it.
        int residue =
            collection.Rolls.Residue < laser.ResidueChance
                ? (int)Math.Min(remainingUnits, RoundedUnits(planned * laser.ResidueMultiplier, collection.Rolls.ResidueYield))
                : 0;
        int extracted = (int)Math.Min(remainingUnits - residue, planned);
        // Bonus ore does not reduce the deposit, and still occupies the hold.
        int bonus =
            collection.Completed && collection.Rolls.Critical < laser.CriticalChance
                ? Math.Min(free - extracted, RoundedUnits(extracted * laser.CriticalBonusYield, collection.Rolls.CriticalYield))
                : 0;
        return new(extracted, bonus, residue, free <= extracted + bonus);
    }

    private static bool Probability(double value) => double.IsFinite(value) && value >= 0 && value <= 1;

    private static bool Roll(double value) => Probability(value) && value < 1;

    private static int RoundedUnits(double units, double roll)
    {
        if (units >= int.MaxValue)
        {
            return int.MaxValue;
        }
        // Ignore floating-point noise at whole-unit boundaries, not real fractions.
        double whole = Math.Floor(units + InventoryDefinitions.ResourceTolerance);
        return checked((int)whole + (roll < units - whole ? 1 : 0));
    }

    public static IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(MiningModuleInputs inputs)
    {
        var attributes = inputs.Attributes.ToDictionary();
        if (inputs.GroupId != InventoryDefinitions.MiningLaserGroup)
        {
            return attributes;
        }
        attributes[DogmaAttributeId.MiningAmount] =
            attributes.GetValueOrDefault(DogmaAttributeId.MiningAmount)
            * (1 + MiningDefinitions.YieldBonusPerSkillLevel * inputs.Levels.GetValueOrDefault(MiningSkill.Mining))
            * (1 + MiningDefinitions.YieldBonusPerSkillLevel * inputs.Levels.GetValueOrDefault(MiningSkill.Astrogeology));
        if (inputs.HullTypeId == (int)StarterShipType.Venture)
        {
            // Build 3662168 effects 5058/5139 multiply ore yield by hull attribute
            // 207, then apply attribute 1842 percent for each Mining Frigate level.
            var hull = inputs.HullAttributes;
            attributes[DogmaAttributeId.MiningAmount] *=
                hull.GetValueOrDefault(DogmaAttributeId.MiningRoleYieldMultiplier, 1)
                * (
                    1
                    + hull.GetValueOrDefault(DogmaAttributeId.MiningFrigateYieldBonus)
                        * inputs.Levels.GetValueOrDefault(MiningSkill.Frigate)
                        / 100
                );
        }
        attributes[DogmaAttributeId.MiningCriticalChance] =
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalChance)
            * (1 + MiningDefinitions.CriticalChanceBonusPerSkillLevel * inputs.Levels.GetValueOrDefault(MiningSkill.Precision));
        attributes[DogmaAttributeId.MiningCriticalBonusYield] =
            attributes.GetValueOrDefault(DogmaAttributeId.MiningCriticalBonusYield)
            * (1 + MiningDefinitions.CriticalYieldBonusPerSkillLevel * inputs.Levels.GetValueOrDefault(MiningSkill.Exploitation));
        return attributes;
    }
}
