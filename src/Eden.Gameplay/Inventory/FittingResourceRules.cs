using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;

namespace Eden.Gameplay.Inventory;

public sealed record FittingShutdownCandidate(long ItemId, double Cpu, double Power);

public sealed record FittingResourceChange(double Shield, double ArmorDamage, double StructureDamage, double Capacitor);

public static class FittingResourceRules
{
    public static long? Shutdown(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        IReadOnlyList<FittingShutdownCandidate> candidates
    )
    {
        double cpu = attributes.GetValueOrDefault(DogmaAttributeId.CpuLoad);
        double power = attributes.GetValueOrDefault(DogmaAttributeId.PowerLoad);
        double cpuOutput = attributes.GetValueOrDefault(DogmaAttributeId.CpuOutput);
        double powerOutput = attributes.GetValueOrDefault(DogmaAttributeId.PowerOutput);
        if (cpu <= cpuOutput + InventoryDefinitions.ResourceTolerance && power <= powerOutput + InventoryDefinitions.ResourceTolerance)
        {
            return null;
        }
        bool grid =
            power / Math.Max(powerOutput, InventoryDefinitions.ResourceTolerance)
            > cpu / Math.Max(cpuOutput, InventoryDefinitions.ResourceTolerance);
        return candidates
                .Where(c => (grid ? c.Power : c.Cpu) > 0)
                .OrderByDescending(c => grid ? c.Power : c.Cpu)
                .ThenByDescending(c => c.ItemId)
                .Select(c => (long?)c.ItemId)
                .FirstOrDefault()
            ?? throw new InvalidDataException("The fitting cannot be brought within CPU and power grid limits.");
    }

    public static FittingResourceChange Resize(
        ShipResourceSnapshot before,
        IReadOnlyDictionary<DogmaAttributeId, double> after,
        double usedCargo
    )
    {
        double capacity = after.GetValueOrDefault(DogmaAttributeId.CargoCapacity);
        if (usedCargo > capacity + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(capacity, usedCargo);
        }
        var defense = ShipHealth.DefenseFrom(after);
        double armorDamage = before.Health.Defense.Armor - before.Health.Remaining.Armor;
        double hullDamage = before.Health.Defense.Structure - before.Health.Remaining.Structure;
        if (hullDamage >= defense.Structure)
        {
            throw new InvalidDataException("This fitting change would leave the ship with no structure.");
        }
        return new(
            ChargedResource.Resize(before.Health.Remaining.Shield, before.Health.Defense.Shield, defense.Shield),
            armorDamage,
            hullDamage,
            ChargedResource.Resize(
                before.Capacitor,
                before.Attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity),
                after.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            )
        );
    }
}
