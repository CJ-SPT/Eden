using Eden.Gameplay.Combat;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

[Injectable]
internal sealed class PilotShipResourceStore(WorldDatabase database) : IPilotShipResources
{
    public ShipResourceSnapshot Read(long pilotId, long shipId, long now)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId], ships: [shipId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId && p.ActiveShipId == shipId);
        var attributes = PilotFittingStore.ShipAttributes(world, catalog, pilot);
        ShipResourceSnapshot result = new(
            PilotHealthStore.Read(world, shipId, ShipHealth.DefenseFrom(attributes), now),
            ShipResources.Capacitor(world, shipId, attributes, now),
            attributes
        );
        transaction.Commit();
        return result;
    }

    public double Capacitor(long pilotId, long now)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        double result = ShipResources.Capacitor(world, pilot.ActiveShipId, PilotFittingStore.ShipAttributes(world, catalog, pilot), now);
        transaction.Commit();
        return result;
    }
}
