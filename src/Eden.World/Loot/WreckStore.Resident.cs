using System.Globalization;
using System.Security.Cryptography;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

internal sealed partial class WreckStore
{
    internal static IReadOnlyList<InventoryChange> Take(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long wreckId,
        IReadOnlyList<long> itemIds,
        int? quantity,
        long now,
        InventoryFlag destinationFlag = InventoryFlag.Cargo
    )
    {
        var (pilot, _) = Access(world, catalog, pilotId, wreckId, now);
        var changes = SpaceLootTransfer.Take(world, catalog, pilot, shipId, wreckId, itemIds, quantity, destinationFlag);
        world.SaveChanges();
        return changes;
    }

    private static (Pilot Pilot, WorldWreck Wreck) Access(WorldAction world, CatalogDbContext catalog, long pilotId, long wreckId, long now)
    {
        var pilot = world.Pilots.Single(p => p.Id == pilotId);
        var wreck = world.Set<WorldWreck>().Find(wreckId);
        if (
            wreck is null
            || wreck.ExpiresAt <= now
            || wreck.OwnerId != pilotId
            || pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != wreck.SystemId
        )
        {
            throw new InvalidDataException("The wreck is unavailable or you do not have its loot rights.");
        }
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        var type = catalog.RequireItem(wreck.TypeId);
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(new(wreck.X, wreck.Y, wreck.Z))
                - (hull.Radius ?? 0)
                - (type.Radius ?? 0)
            > WreckDefinitions.TransferRange
        )
        {
            throw new InvalidDataException("Approach within 2,500 m of the wreck to transfer its contents.");
        }
        return (pilot, wreck);
    }
}
