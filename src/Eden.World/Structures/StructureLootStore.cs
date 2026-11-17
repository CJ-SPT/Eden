using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Structures;
using Eden.World.Catalog;
using Eden.World.Information;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime.Economy;

namespace Eden.World.Structures;

internal static class StructureLootStore
{
    internal static IReadOnlyList<WreckContact> Contacts(WorldAction world, CatalogDbContext catalog, int system, long now)
    {
        var type = catalog.RequireItem(JetcanRules.TypeId);
        return world
            .Filter<SavedStructureLoot>(c => c.SystemId == system && c.ExpiresAt > now)
            .OrderBy(c => c.Id)
            .AsEnumerable()
            .Select(c => new WreckContact(
                c.Id,
                c.StructureId,
                c.OwnerId,
                c.CorporationId,
                JetcanRules.TypeId,
                c.Name,
                system,
                new(c.X, c.Y, c.Z),
                type.Mass ?? 0,
                (float)(type.Radius ?? 0),
                !world.Filter<OwnedItem>(i => i.LocationId == c.Id).Any() && !world.Filter<Ship>(s => s.StoredInId == c.Id).Any(),
                type.GroupId,
                type.CategoryId,
                Abandoned: true
            ))
            .ToArray();
    }

    internal static WreckInventory Read(WorldAction world, CatalogDbContext catalog, long pilotId, long containerId, long now)
    {
        var (_, container) = Access(world, catalog, pilotId, containerId, now);
        var type = catalog.RequireItem(JetcanRules.TypeId);
        return new(
            new(
                container.Id,
                type.Id,
                container.OwnerId,
                container.SystemId,
                (int)InventoryFlag.AutoFit,
                1,
                type.GroupId,
                type.CategoryId,
                PilotInventoryStore.Volume(catalog, type.Id),
                true
            ),
            PilotInventoryStore.List(world, catalog, container.OwnerId, container.Id)
        );
    }

    internal static IReadOnlyList<InventoryChange> Take(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long containerId,
        IReadOnlyList<long> items,
        int? quantity,
        long now,
        InventoryFlag flag
    )
    {
        var (pilot, container) = Access(world, catalog, pilotId, containerId, now);
        var changes = SpaceLootTransfer.Take(world, catalog, pilot, shipId, containerId, items, quantity, flag, container.OwnerId);
        if (container.OwnerId != pilotId)
        {
            var moved = changes
                .Where(c => c.Item.LocationId == shipId && c.Item.OwnerId == pilotId && c.Previous?.Id == c.Item.Id)
                .Select(c => c.Item.Id)
                .ToArray();
            foreach (long root in moved)
            {
                var contents = Descendants(world, root);
                foreach (var child in contents.Items)
                {
                    child.OwnerId = pilotId;
                }
                foreach (var hull in contents.Ships)
                {
                    PilotInsuranceStore.VoidRemovedHull(world, hull.Id);
                    hull.OwnerId = pilotId;
                }
            }
        }
        return changes;
    }

    private static (Pilot Pilot, SavedStructureLoot Container) Access(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long containerId,
        long now
    )
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Choose your selected pilot.");
        var container = world.Set<SavedStructureLoot>().Find(containerId);
        var ship = pilot.ActiveShip;
        var type = catalog.RequireItem(JetcanRules.TypeId);
        if (
            container is null
            || container.ExpiresAt <= now
            || ship.Position is not { } position
            || !AssetSafetyRules.CanAccessLoot(
                position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId,
                container.SystemId,
                new(position.X, position.Y, position.Z),
                new(container.X, container.Y, container.Z),
                catalog.RequireItem(ship.TypeId).Radius ?? 0,
                type.Radius ?? 0
            )
        )
        {
            throw new InvalidDataException("Approach within 2,500 m of this available structure loot container.");
        }
        world.RequireUncloaked(ship.Id);
        return (pilot, container);
    }

    internal static void Expire(WorldAction world, int system, long now)
    {
        foreach (var container in world.Filter<SavedStructureLoot>(c => c.SystemId == system && c.ExpiresAt <= now).ToArray())
        {
            var contents = Descendants(world, container.Id);
            foreach (var ship in contents.Ships)
            {
                PilotInsuranceStore.VoidRemovedHull(world, ship.Id);
                world.Remove(ship);
            }
            world.RemoveRange(contents.Items);
            world.Remove(container);
        }
    }

    private sealed record Contents(IReadOnlyList<OwnedItem> Items, IReadOnlyList<Ship> Ships);

    private static Contents Descendants(WorldAction world, long root)
    {
        Queue<long> pending = new([root]);
        HashSet<long> visited = [];
        List<OwnedItem> items = [];
        List<Ship> ships = [];
        while (pending.TryDequeue(out long parent))
        {
            if (!visited.Add(parent))
            {
                throw new InvalidDataException("Structure loot contains a cyclic inventory.");
            }
            foreach (var item in world.Filter<OwnedItem>(i => i.LocationId == parent))
            {
                items.Add(item);
                pending.Enqueue(item.Id);
            }
            foreach (var ship in world.Filter<Ship>(s => s.StoredInId == parent))
            {
                ships.Add(ship);
                pending.Enqueue(ship.Id);
            }
        }
        return new(items, ships);
    }
}
