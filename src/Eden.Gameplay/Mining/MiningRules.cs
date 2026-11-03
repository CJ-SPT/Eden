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
    public sealed record Collection(int Extracted, int CriticalBonus, bool CargoFull);

    public static Collection Collect(
        long remainingUnits,
        double availableVolume,
        double oreVolume,
        double cycleVolume,
        double criticalBonus
    )
    {
        if (!(oreVolume > 0) || !double.IsFinite(oreVolume))
        {
            throw new InvalidDataException("Invalid ore volume.");
        }
        if (cycleVolume < 0 || !double.IsFinite(cycleVolume) || criticalBonus < 0 || !double.IsFinite(criticalBonus))
        {
            throw new InvalidDataException("Invalid mining yield.");
        }
        int free = (int)Math.Min(int.MaxValue, Math.Floor((availableVolume + InventoryDefinitions.ResourceTolerance) / oreVolume));
        int extracted = (int)
            Math.Min(Math.Min(remainingUnits, free), Math.Floor((cycleVolume + InventoryDefinitions.ResourceTolerance) / oreVolume));
        // Bonus ore does not reduce the deposit, and still occupies the hold.
        int bonus = (int)Math.Min(free - extracted, Math.Floor(extracted * criticalBonus));
        return new(extracted, bonus, free <= extracted + bonus);
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
