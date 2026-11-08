using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

[Injectable]
internal sealed class PilotShipHeatStore(WorldDatabase database) : IPilotShipHeat
{
    public ShipHeatSnapshot Read(long pilotId, long shipId, long now)
    {
        if (now < 0)
        {
            throw new InvalidDataException("Invalid ship heat clock.");
        }
        if (LiveWorld.Find(database.Path)?.Heat is { } resident)
        {
            return resident.Read(pilotId, shipId, now);
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var loaded = ShipHeatPersistence.Load(world, catalog, shipId, now, expectedPilot: pilotId);
        if (loaded is null || loaded.PilotId != pilotId)
        {
            throw new InvalidDataException("Read heat only on an owned ship.");
        }
        transaction.Commit();
        return loaded.State;
    }
}
