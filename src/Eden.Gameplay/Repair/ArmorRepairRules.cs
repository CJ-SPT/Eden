using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Repair;

public static class ArmorRepairRules
{
    public static ArmorRepairer Define(long itemId, int typeId, IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        ArmorRepairer repairer = new(
            itemId,
            typeId,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(DogmaAttributeId.ArmorRepairAmount),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)
        );
        if (
            !double.IsFinite(repairer.DurationMilliseconds)
            || repairer.DurationMilliseconds <= 0
            || !double.IsFinite(repairer.ArmorAmount)
            || repairer.ArmorAmount <= 0
            || !double.IsFinite(repairer.CapacitorNeed)
            || repairer.CapacitorNeed < 0
        )
        {
            throw new InvalidDataException("Invalid armor repair cycle attributes.");
        }
        return repairer;
    }

    public static ShipHealth Apply(ShipHealth health, double amount)
    {
        if (!double.IsFinite(amount) || amount <= 0)
        {
            throw new InvalidDataException("Invalid paid armor repair amount.");
        }
        return health.Remaining.Destroyed
            ? health
            : health with
            {
                Remaining = health.Remaining with { Armor = Math.Min(health.Defense.Armor, health.Remaining.Armor + amount) },
            };
    }
}
