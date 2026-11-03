using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Repair;

public static class LocalRepairRules
{
    public static long CompletesAt(long startedAt, LocalRepairer repairer)
    {
        if (!double.IsFinite(repairer.DurationMilliseconds) || repairer.DurationMilliseconds <= 0)
        {
            throw new InvalidDataException("Invalid local repair duration.");
        }
        // Convert the catalog duration before rounding to FILETIME ticks. A
        // binary artifact such as 36000.00000000001 must not add a paid tick.
        long ticks = checked((long)decimal.Ceiling((decimal)repairer.DurationMilliseconds * TimeSpan.TicksPerMillisecond));
        return checked(startedAt + ticks);
    }

    public static LocalRepairer Define(
        long itemId,
        int typeId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        RepairLayer layer = RepairLayer.Armor
    )
    {
        LocalRepairer repairer = new(
            itemId,
            typeId,
            attributes.GetValueOrDefault(DogmaAttributeId.Duration),
            attributes.GetValueOrDefault(LocalRepairDefinitions.Profile(layer).AmountAttribute),
            attributes.GetValueOrDefault(DogmaAttributeId.CapacitorNeed)
        );
        if (
            !double.IsFinite(repairer.DurationMilliseconds)
            || repairer.DurationMilliseconds <= 0
            || !double.IsFinite(repairer.Amount)
            || repairer.Amount <= 0
            || !double.IsFinite(repairer.CapacitorNeed)
            || repairer.CapacitorNeed < 0
        )
        {
            throw new InvalidDataException("Invalid local repair cycle attributes.");
        }
        return repairer;
    }

    public static ShipHealth Apply(ShipHealth health, double amount, RepairLayer layer = RepairLayer.Armor)
    {
        if (!double.IsFinite(amount) || amount <= 0)
        {
            throw new InvalidDataException("Invalid paid local repair amount.");
        }
        _ = LocalRepairDefinitions.Profile(layer);
        return health.Remaining.Destroyed
            ? health
            : health with
            {
                Remaining =
                    layer == RepairLayer.Armor
                        ? health.Remaining with
                        {
                            Armor = Math.Min(health.Defense.Armor, health.Remaining.Armor + amount),
                        }
                        : health.Remaining with
                        {
                            Structure = Math.Min(health.Defense.Structure, health.Remaining.Structure + amount),
                        },
            };
    }
}
