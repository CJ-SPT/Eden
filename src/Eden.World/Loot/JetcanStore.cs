using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

[Injectable]
internal sealed class JetcanStore(WorldDatabase database) : ICargoJettison
{
    public JettisonResult Jettison(long pilotId, long shipId, IReadOnlyList<long> itemIds, long now)
    {
        if (itemIds.Count is 0 or > JetcanRules.MaximumItemsPerCall)
        {
            return new([]);
        }
        using WorldDbContext world = new(database.Path) { Participants = new(pilots: [pilotId], ships: [shipId]) };
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (pilot, system, position) = InSpace(world, catalog, pilotId);
        var ship = pilot.ActiveShip;
        var hull = catalog.RequireItem(ship.TypeId);
        if (ship.Id != shipId || hull.GroupId == FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("Jettison cargo from your active ship in space.");
        }
        var cooldown = world.Set<SavedJettisonCooldown>().Find(pilotId);
        if (cooldown is not null && cooldown.ReadyAt > now)
        {
            return new([], cooldown.ReadyAt - now);
        }
        var type = catalog.RequireItem(JetcanRules.TypeId);
        var items = LoadCargo(world, catalog, pilot, itemIds).ToDictionary(i => i.Row.Id);
        List<Cargo> selected = [];
        double available = Capacity(type);
        foreach (long id in itemIds.Distinct())
        {
            if (
                items.TryGetValue(id, out var cargo)
                && cargo.Item.Volume * cargo.Item.Quantity <= available + InventoryDefinitions.ResourceTolerance
            )
            {
                selected.Add(cargo);
                available -= cargo.Item.Volume * cargo.Item.Quantity;
            }
        }
        if (selected.Count == 0)
        {
            return new([]);
        }

        var point = JetcanRules.LaunchPosition(position, hull.Radius ?? 0, type.Radius ?? 0);
        var can = world
            .Add(
                new SavedJetcan
                {
                    OwnerId = pilotId,
                    ShipId = ship.Id,
                    CorporationId = pilot.CorporationId,
                    SystemId = system,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    ExpiresAt = checked(now + JetcanRules.Lifetime),
                }
            )
            .Entity;
        world.SaveChanges();

        cooldown ??= world.Add(new SavedJettisonCooldown { PilotId = pilotId }).Entity;
        cooldown.ReadyAt = checked(now + JetcanRules.Cooldown);
        var changes = selected.Select(i => Move(i, can.Id)).ToArray();
        world.SaveChanges();
        transaction.Commit();
        return new(changes);
    }

    public IReadOnlyList<InventoryChange> Deposit(long pilotId, long canId, InventoryMoveRequest request, long now)
    {
        using WorldDbContext world = new(database.Path) { Participants = new(pilots: [pilotId]) };
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (pilot, can) = Access(world, catalog, pilotId, canId, now);
        var (ids, source, flag, quantity) = request;
        if (
            source != pilot.ActiveShipId
            || flag != (int)InventoryFlag.None
            || ids.Count is 0 or > JetcanRules.MaximumItemsPerCall
            || ids.Distinct().Count() != ids.Count
            || quantity is <= 0
            || quantity.HasValue && ids.Count != 1
        )
        {
            throw new InvalidDataException("Select cargo in your active ship to move into this container.");
        }
        var items = LoadCargo(world, catalog, pilot, ids);
        if (items.Length != ids.Count || items.Any(i => quantity > i.Item.Quantity))
        {
            throw new InvalidDataException("The requested cargo or quantity is unavailable.");
        }
        double capacity = Capacity(catalog.RequireItem(JetcanRules.TypeId));
        double used = Contents(world, catalog, can.Id).Sum(i => i.Volume * i.Quantity);
        double added = items.Sum(i => i.Item.Volume * (quantity ?? i.Item.Quantity));
        if (added > capacity - used + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(Math.Max(0, capacity - used), added);
        }
        var changes = items.SelectMany(i => Transfer(world, catalog, i, can.Id, quantity ?? i.Item.Quantity)).ToArray();
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    private sealed record Cargo(OwnedItem Row, InventoryItem Item);

    private static Cargo[] LoadCargo(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, IReadOnlyList<long> ids) =>
        world
            .Set<OwnedItem>()
            .Where(i =>
                ids.Contains(i.Id)
                && i.OwnerId == pilot.Id
                && i.LocationId == pilot.ActiveShipId
                && !i.Online
                && i.TypeId != CourierRules.PackageType
                && !world.Set<IndustryJob>().Any(j => j.BlueprintId == i.Id && j.Status == IndustryJobStatus.Installed)
                && !world.Set<OwnedItem>().Any(child => child.LocationId == i.Id)
                && !world.Ships.Any(child => child.StoredInId == i.Id)
            )
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Select(i => new Cargo(i, PilotInventoryStore.Read(catalog, i)))
            .Where(i => JetcanRules.CanJettison(i.Item))
            .ToArray();

    private static InventoryChange Move(Cargo cargo, long destination)
    {
        cargo.Row.LocationId = destination;
        cargo.Row.FlagId = (int)InventoryFlag.None;
        return new(cargo.Item with { LocationId = destination, FlagId = (int)InventoryFlag.None }, cargo.Item);
    }

    private static IReadOnlyList<InventoryChange> Transfer(
        WorldDbContext world,
        CatalogDbContext catalog,
        Cargo cargo,
        long destination,
        int quantity
    ) =>
        quantity == cargo.Item.Quantity
            ? [Move(cargo, destination)]
            : InventoryPersistence.SplitTransfer(world, catalog, cargo.Row, cargo.Item, destination, (int)InventoryFlag.None, quantity);

    private static (Pilot Pilot, int System, SpacePosition Position) InSpace(WorldDbContext world, CatalogDbContext catalog, long pilotId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId);
        if (pilot?.ActiveShip.Position is not { } position)
        {
            throw new InvalidDataException("Access jettisoned cargo from a ship in space.");
        }
        PilotSpatialModuleStore.RequireUncloaked(world, pilot.ActiveShipId);
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        return (pilot, system, new(position.X, position.Y, position.Z));
    }

    internal static (Pilot Pilot, SavedJetcan Can) Access(WorldDbContext world, CatalogDbContext catalog, long pilotId, long id, long now)
    {
        var (pilot, system, position) = InSpace(world, catalog, pilotId);
        var can = world.Set<SavedJetcan>().Find(id);
        if (can is null || can.ExpiresAt <= now || can.SystemId != system || can.OwnerId != pilotId)
        {
            throw new InvalidDataException("The cargo container is unavailable or you do not have its loot rights.");
        }
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        var type = catalog.RequireItem(JetcanRules.TypeId);
        if (position.DistanceTo(new(can.X, can.Y, can.Z)) - (hull.Radius ?? 0) - (type.Radius ?? 0) > WreckDefinitions.TransferRange)
        {
            throw new InvalidDataException("Approach within 2,500 m of the cargo container to transfer its contents.");
        }
        return (pilot, can);
    }

    private static double Capacity(CatalogItemDefinition type) =>
        type is { Capacity: > 0, GroupId: JetcanRules.GroupId }
            ? type.Capacity.Value
            : throw new InvalidDataException("Missing jetcan capacity.");

    private static InventoryItem[] Contents(WorldDbContext world, CatalogDbContext catalog, long id) =>
        world
            .Set<OwnedItem>()
            .Where(i => i.LocationId == id)
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Select(i => PilotInventoryStore.Read(catalog, i))
            .ToArray();

    internal static WreckInventory Read(WorldDbContext world, CatalogDbContext catalog, long pilotId, long id, long now)
    {
        var (_, can) = Access(world, catalog, pilotId, id, now);
        var type = catalog.RequireItem(JetcanRules.TypeId);
        var item = new InventoryItem(
            can.Id,
            type.Id,
            can.OwnerId,
            can.SystemId,
            (int)InventoryFlag.None,
            1,
            type.GroupId,
            type.CategoryId,
            PilotInventoryStore.Volume(catalog, type.Id),
            Singleton: true
        );
        return new(item, Contents(world, catalog, id), Capacity(type));
    }

    internal static WreckContact[] Contacts(WorldDbContext world, CatalogDbContext catalog, int system, long now)
    {
        var rows = world
            .Set<SavedJetcan>()
            .AsNoTracking()
            .Where(c => c.SystemId == system && c.ExpiresAt > now)
            .OrderBy(c => c.Id)
            .ToArray();
        if (rows.Length == 0)
        {
            return [];
        }
        var type = catalog.RequireItem(JetcanRules.TypeId);
        return rows.Select(c => new WreckContact(
                c.Id,
                c.ShipId,
                c.OwnerId,
                c.CorporationId,
                type.Id,
                type.Name,
                system,
                new(c.X, c.Y, c.Z),
                type.Mass ?? 0,
                (float)(type.Radius ?? 0),
                false,
                type.GroupId,
                type.CategoryId
            ))
            .ToArray();
    }

    internal static void Expire(WorldDbContext world, long now, int? system = null)
    {
        var expired = world.Set<SavedJetcan>().Where(c => c.ExpiresAt <= now && (!system.HasValue || c.SystemId == system));
        world.Set<OwnedItem>().Where(i => expired.Any(c => c.Id == i.LocationId)).ExecuteDelete();
        expired.ExecuteDelete();
    }
}
