using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;

namespace Eden.Server.Persistence;

internal static class ShipResources
{
    internal static double Capacitor(WorldDbContext world, long shipId, IReadOnlyDictionary<DogmaAttributeId, double> attributes, long now)
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

    internal static double SpendCapacitor(
        WorldDbContext world,
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
