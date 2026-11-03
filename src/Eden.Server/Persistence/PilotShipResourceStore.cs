using Eden.Gameplay.Combat;
using Eden.Gameplay.Ships;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotShipResourceStore(WorldDatabase database) : IPilotShipResources
{
    public ShipResourceSnapshot Read(long pilotId, long shipId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId && p.ActiveShipId == shipId);
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
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        return ShipResources.Capacitor(world, pilot.ActiveShipId, PilotFittingStore.ShipAttributes(world, catalog, pilot), now);
    }
}
