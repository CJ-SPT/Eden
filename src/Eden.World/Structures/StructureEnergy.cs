using Eden.Data.World.Structures;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;

namespace Eden.World.Structures;

internal static class StructureEnergy
{
    internal static double CostMultiplier(WorldAction world, CatalogDbContext catalog, long pilot, int skill)
    {
        int level = PilotSkillReader.Level(world, catalog, pilot, skill);
        double bonus = catalog.FittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.CapacitorNeedBonus);
        return StructureWeaponRules.CapacitorCostMultiplier(level, bonus);
    }

    internal static CapacitorPool Read(WorldAction world, long id, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
    {
        var saved = world.Set<SavedStructureEnergy>().Find(id);
        double capacity = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity);
        double charge = saved is null
            ? capacity
            : ChargedResource.Recharged(
                saved.Charge,
                capacity,
                attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
                Math.Max(0, now - saved.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond
            );
        return new(charge, capacity);
    }

    internal static void Write(WorldAction world, long id, double charge, long now)
    {
        var saved = world.Set<SavedStructureEnergy>().Find(id) ?? world.Add(new SavedStructureEnergy { StructureId = id }).Entity;
        saved.Charge = charge;
        saved.UpdatedAt = Math.Max(saved.UpdatedAt, now);
    }

    internal static void Resize(
        WorldAction world,
        long id,
        IReadOnlyDictionary<DogmaAttributeId, double> before,
        IReadOnlyDictionary<DogmaAttributeId, double> after,
        long now
    )
    {
        double capacity = after.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity);
        double recharge = after.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime);
        if (
            capacity == before.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity)
            && recharge == before.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime)
        )
        {
            return;
        }
        if (world.Set<SavedStructureEnergy>().Find(id) is not null)
        {
            var pool = Read(world, id, before, now);
            Write(world, id, ChargedResource.Resize(pool.Charge, pool.Capacity, capacity), now);
        }
    }
}
