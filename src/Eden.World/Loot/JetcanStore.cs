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
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

internal sealed record JetcanView(IReadOnlyList<WreckContact> Contacts, long NextExpiry);

[Injectable]
internal sealed class JetcanStore(WorldDatabase database) : ICargoJettison
{
    public JettisonResult Jettison(long pilotId, long shipId, IReadOnlyList<long> itemIds, long now)
    {
        using WorldActionScope action = new(database, new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = Jettison(action.World, catalog, pilotId, shipId, itemIds, now);
        action.Commit();
        return result;
    }

    internal static JettisonResult Jettison(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        IReadOnlyList<long> itemIds,
        long now,
        bool allowSplit = false
    )
    {
        if (itemIds.Count is 0 or > JetcanRules.MaximumItemsPerCall)
        {
            return new([]);
        }
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
        List<(Cargo Cargo, int Quantity)> selected = [];
        double available = Capacity(type);
        foreach (long id in itemIds.Distinct())
        {
            if (!items.TryGetValue(id, out var cargo))
            {
                continue;
            }
            var item = cargo.Item;
            double remaining = available + InventoryDefinitions.ResourceTolerance;
            if (item.Volume * item.Quantity <= remaining)
            {
                selected.Add((cargo, item.Quantity));
                available -= item.Volume * item.Quantity;
            }
            else if (allowSplit && !item.Singleton && item.Volume > 0 && item.Volume <= remaining)
            {
                int quantity = checked((int)Math.Floor(remaining / item.Volume));
                selected.Add((cargo, quantity));
                available -= item.Volume * quantity;
            }
        }
        if (selected.Count == 0)
        {
            return new([]);
        }

        long canId = world.Next("WorldJetcan", JetcanRules.FirstId - 1);
        if (!JetcanRules.IsJetcan(canId))
        {
            throw new InvalidDataException("No container identity is available.");
        }
        var point = JetcanRules.LaunchPosition(position, hull.Radius ?? 0, type.Radius ?? 0);
        var ownerAccess = SpaceLootRights.OwnerAccess(world, pilotId, now);
        var can = world
            .Add(
                new SavedJetcan
                {
                    Id = canId,
                    OwnerId = pilotId,
                    ShipId = ship.Id,
                    CorporationId = pilot.CorporationId,
                    SystemId = system,
                    X = point.X,
                    Y = point.Y,
                    Z = point.Z,
                    ExpiresAt = checked(now + JetcanRules.Lifetime),
                    Abandoned = LootRightsRules.AbandonedAtCreation(ownerAccess, playerWreck: false),
                }
            )
            .Entity;
        cooldown ??= world.Add(new SavedJettisonCooldown { PilotId = pilotId }).Entity;
        cooldown.ReadyAt = checked(now + JetcanRules.Cooldown);
        var changes = selected.SelectMany(stack => Transfer(world, catalog, stack.Cargo, can.Id, stack.Quantity)).ToArray();
        return new(changes, Contact: Contact(type, can, ownerAccess.SecurityStatus));
    }

    public IReadOnlyList<InventoryChange> Deposit(long pilotId, long canId, InventoryMoveRequest request, long now)
    {
        using WorldActionScope action = new(database, new(pilots: [pilotId]));
        var world = action.World;
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
        action.Commit();
        return changes;
    }

    private sealed record Cargo(OwnedItem Row, InventoryItem Item);

    private static Cargo[] LoadCargo(WorldAction world, CatalogDbContext catalog, Pilot pilot, IReadOnlyList<long> ids)
    {
        long ship = pilot.ActiveShipId;
        return ids.Distinct()
            .Select(id => world.Set<OwnedItem>().Find(id))
            .OfType<OwnedItem>()
            .Where(item => item.OwnerId == pilot.Id && item.LocationId == ship && !item.Online && item.TypeId != CourierRules.PackageType)
            .Select(item => new Cargo(item, PilotInventoryStore.Read(catalog, item)))
            .Where(cargo =>
                JetcanRules.CanJettison(cargo.Item)
                && !world.Filter<OwnedItem>(child => child.LocationId == cargo.Row.Id).Any()
                && !world.Filter<Ship>(child => child.StoredInId == cargo.Row.Id).Any()
                && !world.Filter<IndustryJob>(job => job.BlueprintId == cargo.Row.Id && job.Status == IndustryJobStatus.Installed).Any()
            )
            .OrderBy(cargo => cargo.Row.Id)
            .ToArray();
    }

    private static InventoryChange Move(Cargo cargo, long destination)
    {
        cargo.Row.LocationId = destination;
        cargo.Row.FlagId = (int)InventoryFlag.None;
        return new(cargo.Item with { LocationId = destination, FlagId = (int)InventoryFlag.None }, cargo.Item);
    }

    private static IReadOnlyList<InventoryChange> Transfer(
        WorldAction world,
        CatalogDbContext catalog,
        Cargo cargo,
        long destination,
        int quantity
    ) =>
        quantity == cargo.Item.Quantity
            ? [Move(cargo, destination)]
            : InventoryPersistence.SplitTransfer(world, catalog, cargo.Row, cargo.Item, destination, (int)InventoryFlag.None, quantity);

    private static (Pilot Pilot, int System, SpacePosition Position) InSpace(WorldAction world, CatalogDbContext catalog, long pilotId)
    {
        var pilot = world.Pilots.Find(pilotId);
        if (pilot?.ActiveShip.Position is not { } position)
        {
            throw new InvalidDataException("Access jettisoned cargo from a ship in space.");
        }
        world.RequireUncloaked(pilot.ActiveShipId);
        int system = position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId;
        return (pilot, system, new(position.X, position.Y, position.Z));
    }

    internal static (Pilot Pilot, SavedJetcan Can) Access(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long id,
        long now,
        bool allowForeign = false
    )
    {
        var (pilot, system, position) = InSpace(world, catalog, pilotId);
        var can = world.Set<SavedJetcan>().Find(id);
        if (can is null || can.ExpiresAt <= now || can.SystemId != system || (can.OwnerId != pilotId && !allowForeign))
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

    private static InventoryItem[] Contents(WorldAction world, CatalogDbContext catalog, long id) =>
        world
            .Filter<OwnedItem>(i => i.LocationId == id)
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Select(i => PilotInventoryStore.Read(catalog, i))
            .ToArray();

    internal static WreckInventory Read(WorldAction world, CatalogDbContext catalog, long pilotId, long id, long now)
    {
        var (_, can) = Access(world, catalog, pilotId, id, now, allowForeign: true);
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

    internal static WreckContact[] Contacts(WorldAction world, CatalogDbContext catalog, IReadOnlyList<SavedJetcan> rows)
    {
        if (rows.Count == 0)
        {
            return [];
        }
        var type = catalog.RequireItem(JetcanRules.TypeId);
        var security = SpaceLootRights.SecurityStatuses(world, rows.Select(can => can.OwnerId));
        return rows.Select(can => Contact(type, can, security.GetValueOrDefault(can.OwnerId))).ToArray();
    }

    private static WreckContact Contact(CatalogItemDefinition type, SavedJetcan can, double security) =>
        new(
            can.Id,
            can.ShipId,
            can.OwnerId,
            can.CorporationId,
            type.Id,
            type.Name,
            can.SystemId,
            new(can.X, can.Y, can.Z),
            type.Mass ?? 0,
            (float)(type.Radius ?? 0),
            false,
            type.GroupId,
            type.CategoryId,
            Abandoned: can.Abandoned,
            OwnerSecurityStatus: security
        );

    internal static SavedJetcan[] List(WorldAction world, int system) =>
        world.Filter<SavedJetcan>(can => can.SystemId == system).OrderBy(can => can.Id).ToArray();

    internal static JetcanView ReadContacts(LiveWorld live, WorldDatabase database, int system, long now)
    {
        using var ownership = live.EnterShared("jetcan contacts");
        var rows = live.Economy.Inspect(world => List(world, system));
        if (rows.Any(can => can.ExpiresAt <= now))
        {
            using var action = live.Economy.Begin(new(systems: [system]));
            foreach (var can in List(action, system).Where(can => can.ExpiresAt <= now))
            {
                action.Remove(can);
            }
            action.Commit();
        }
        var active = rows.Where(can => can.ExpiresAt > now).ToArray();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var contacts = live.Economy.Inspect(world => Contacts(world, catalog, active));
        return new(Array.AsReadOnly(contacts), active.Select(can => can.ExpiresAt).DefaultIfEmpty(long.MaxValue).Min());
    }

    internal static IReadOnlyList<InventoryChange> Take(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long shipId,
        long canId,
        IReadOnlyList<long> items,
        int? quantity,
        long now,
        InventoryFlag destinationFlag
    )
    {
        var (pilot, can) = Access(world, catalog, pilotId, canId, now, allowForeign: true);
        bool theft = SpaceLootRights.CheckTake(
            world,
            catalog,
            pilot,
            can.SystemId,
            new(can.OwnerId, can.CorporationId, can.Abandoned),
            now
        );
        var changes = SpaceLootTransfer.Take(world, catalog, pilot, shipId, canId, items, quantity, destinationFlag, can.OwnerId);
        if (theft)
        {
            SpaceLootRights.RecordTheft(world, pilot, can.SystemId, now);
        }
        // SQL-backed queries must see the moved items before checking whether the can is empty.
        // This flush remains inside the caller's transaction, together with the theft consequence.
        world.SaveChanges();
        if (!world.Filter<OwnedItem>(item => item.LocationId == canId).Any())
        {
            world.Remove(can);
        }
        return changes;
    }

    internal static void Expire(WorldDbContext world, long now, int? system = null)
    {
        var expired = world.Set<SavedJetcan>().Where(c => c.ExpiresAt <= now && (!system.HasValue || c.SystemId == system));
        world.Set<OwnedItem>().Where(i => expired.Any(c => c.Id == i.LocationId)).ExecuteDelete();
        expired.ExecuteDelete();
    }
}
