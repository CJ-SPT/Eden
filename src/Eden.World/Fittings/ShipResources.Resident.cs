using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Fittings;

internal static partial class ShipResources
{
    internal static double Capacitor(WorldAction world, long shipId, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
    {
        var saved = world.Set<ShipEnergy>().Find(shipId);
        double capacity = attributes.GetValueOrDefault(DogmaAttributeId.CapacitorCapacity);
        return saved is null
            ? capacity
            : ChargedResource.Recharged(
                saved.Charge,
                capacity,
                attributes.GetValueOrDefault(DogmaAttributeId.CapacitorRechargeTime),
                Math.Max(0, (now - saved.UpdatedAt) / (double)TimeSpan.TicksPerMillisecond)
            );
    }

    internal static void ResizeAfterBurnout(
        WorldAction world,
        long ship,
        IReadOnlyDictionary<DogmaAttributeId, double> before,
        IReadOnlyDictionary<DogmaAttributeId, double> after,
        long now
    )
    {
        // Absent balances represent fully charged resources at either capacity.
        // Preserve raw armor and hull damage, even if a previous fit had more HP.
        if (
            world.Set<ShipHealthState>().Find(ship) is { } health
            && Resize(health.ShieldCharge, health.UpdatedAt, DogmaAttributeId.ShieldCapacity, DogmaAttributeId.ShieldRechargeTime)
                is double shield
        )
        {
            health.ShieldCharge = shield;
            health.UpdatedAt = Math.Max(now, health.UpdatedAt);
        }
        if (
            world.Set<ShipEnergy>().Find(ship) is { } energy
            && Resize(energy.Charge, energy.UpdatedAt, DogmaAttributeId.CapacitorCapacity, DogmaAttributeId.CapacitorRechargeTime)
                is double capacitor
        )
        {
            energy.Charge = capacitor;
            energy.UpdatedAt = Math.Max(now, energy.UpdatedAt);
        }

        double? Resize(double charge, long updatedAt, DogmaAttributeId capacity, DogmaAttributeId recharge)
        {
            double previous = before.GetValueOrDefault(capacity);
            double current = after.GetValueOrDefault(capacity);
            double rate = before.GetValueOrDefault(recharge);
            if (previous == current && rate == after.GetValueOrDefault(recharge))
            {
                return null;
            }
            double elapsed = Math.Max(0, now - updatedAt) / (double)TimeSpan.TicksPerMillisecond;
            double settled = ChargedResource.Recharged(charge, previous, rate, elapsed);
            return previous == current ? settled : ChargedResource.Resize(settled, previous, current);
        }
    }

    internal static double SpendCapacitor(
        WorldAction world,
        long shipId,
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double cost,
        long now
    )
    {
        if (!double.IsFinite(cost) || cost < 0)
        {
            throw new InvalidDataException("Invalid module capacitor cost.");
        }
        double charge = Capacitor(world, shipId, attributes, now);
        if (charge + InventoryDefinitions.ResourceTolerance < cost)
        {
            throw new InvalidDataException("Insufficient capacitor to activate the module.");
        }
        var saved = world.Set<ShipEnergy>().Find(shipId) ?? world.Set<ShipEnergy>().Add(new() { ShipId = shipId }).Entity;
        saved.Charge = Math.Max(0, charge - cost);
        saved.UpdatedAt = Math.Max(now, saved.UpdatedAt);
        return saved.Charge;
    }
}
