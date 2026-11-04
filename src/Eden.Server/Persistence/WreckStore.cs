using System.Globalization;
using System.Security.Cryptography;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Loot;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class WorldWreck
{
    public long Id { get; set; }
    public long? NpcId { get; set; }
    public long? ShipId { get; set; }
    public long OwnerId { get; set; }
    public int CorporationId { get; set; }
    public int TypeId { get; set; }
    public int SystemId { get; set; }
    public required string Name { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public long CreatedAt { get; set; }
    public long ExpiresAt { get; set; }
}

[Injectable]
internal sealed class WreckStore(WorldDatabase database) : IWrecks
{
    // Called within the fatal weapon transaction: a failed catalog lookup or
    // bounty debit rolls back the wreck and its contents along with the kill.
    internal static void Create(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        var definition = catalog
            .Set<SourceRecord>()
            .Find("client/types", npc.TypeId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientTypes>();
        var type = definition?.WreckTypeID is long wreckType
            ? catalog.Set<ItemType>().SingleOrDefault(t => t.Id == wreckType && t.GroupId == WreckDefinitions.Group)
            : null;
        if (type is null && npc.Role != NpcRole.Pirate && definition is { WreckTypeID: null })
        {
            // Several pinned customs officials have no wreck link. They leave
            // no wreck instead of borrowing an unrelated ship's wreck.
            return;
        }
        if (type is null)
        {
            throw new InvalidDataException("The defeated NPC has no supported catalog wreck.");
        }
        var wreck = world
            .Set<WorldWreck>()
            .Add(
                new()
                {
                    Id = checked(WreckDefinitions.FirstWreckId + npc.Id - NpcEncounterDefinitions.FirstNpcId),
                    NpcId = npc.Id,
                    OwnerId = pilot.Id,
                    CorporationId = pilot.CorporationId,
                    TypeId = type.Id,
                    SystemId = npc.SystemId,
                    Name = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId).Name + " " + type.Name,
                    X = npc.X,
                    Y = npc.Y,
                    Z = npc.Z,
                    CreatedAt = now,
                    ExpiresAt = checked(now + WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond),
                }
            )
            .Entity;
        foreach (var drop in Drops(catalog, npc))
        {
            _ = catalog.Set<ItemType>().Single(t => t.Id == drop.TypeId);
            world
                .Set<OwnedItem>()
                .Add(
                    new()
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = pilot.Id,
                        TypeId = drop.TypeId,
                        LocationId = wreck.Id,
                        FlagId = (int)InventoryFlag.Cargo,
                        Quantity = drop.Quantity,
                    }
                );
        }
    }

    private static IReadOnlyList<NpcLootDrop> Drops(CatalogDbContext catalog, WorldNpc npc)
    {
        if (npc.Role != NpcRole.Pirate)
        {
            return [];
        }
        // Retained solely for the injected Rookie encounter fixture. Normal
        // populations retire this type and never select it from asteroid pools.
        if (npc.TypeId == NpcEncounterDefinitions.GuristasRookieType)
        {
            return [new(WreckDefinitions.StarterLootType, WreckDefinitions.StarterLootQuantity)];
        }
        int groupId = catalog.Set<ItemType>().Single(t => t.Id == npc.TypeId).GroupId;
        var group = NpcPirateGroups.All.SingleOrDefault(g => g.GroupId == groupId);
        return group is null
            ? []
            : NpcLootRules.Roll(
                group.Faction,
                group.Hull,
                RandomNumberGenerator.GetInt32(NpcLootRules.RollOutcomes),
                RandomNumberGenerator.GetInt32(NpcLootRules.RollOutcomes),
                RandomNumberGenerator.GetInt32(NpcLootRules.ModuleChoices)
            );
    }

    internal static WorldWreck CreatePlayer(
        WorldDbContext world,
        CatalogDbContext catalog,
        Ship ship,
        Pilot pilot,
        int systemId,
        long now,
        IReadOnlyList<OwnedItem> items,
        IReadOnlySet<long> survivors
    )
    {
        var definition = catalog
            .Set<SourceRecord>()
            .Find("client/types", ship.TypeId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientTypes>();
        var type = definition?.WreckTypeID is long wreckType
            ? catalog.Set<ItemType>().SingleOrDefault(t => t.Id == wreckType && t.GroupId == WreckDefinitions.Group)
            : null;
        if (type is null)
        {
            throw new InvalidDataException("The lost ship has no supported catalog wreck.");
        }
        var position = ship.Position ?? throw new InvalidDataException("A destroyed ship must be in space.");
        var wreck = world
            .Set<WorldWreck>()
            .Add(
                new()
                {
                    Id = checked(WreckDefinitions.FirstPlayerWreckId + ship.Id),
                    ShipId = ship.Id,
                    OwnerId = pilot.Id,
                    CorporationId = pilot.CorporationId,
                    TypeId = type.Id,
                    SystemId = systemId,
                    Name = ship.Name + " " + type.Name,
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                    CreatedAt = now,
                    ExpiresAt = checked(now + WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond),
                }
            )
            .Entity;
        // Surviving modules retain their singleton identity but are loose,
        // offline cargo. The old hull's cooldowns must not follow them.
        var ids = items.Select(i => i.Id).ToArray();
        world.Set<ModuleCooldown>().Where(c => ids.Contains(c.ModuleId)).ExecuteDelete();
        foreach (var item in items)
        {
            if (survivors.Contains(item.Id))
            {
                item.LocationId = wreck.Id;
                item.FlagId = (int)InventoryFlag.Cargo;
                item.Online = false;
            }
            else
            {
                world.Remove(item);
            }
        }
        return wreck;
    }

    public IReadOnlyList<WreckContact> List(int systemId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        // Expired containers cannot be opened, even before this lazy cleanup.
        world
            .Set<OwnedItem>()
            .Where(i => world.Set<WorldWreck>().Where(w => w.ExpiresAt <= now).Select(w => w.Id).Contains(i.LocationId))
            .ExecuteDelete();
        var expiredShips = world
            .Ships.Where(s => world.Set<WorldWreck>().Where(w => w.ExpiresAt <= now).Select(w => (long?)w.Id).Contains(s.StoredInId))
            .ToArray();
        foreach (var expired in expiredShips)
        {
            ShipStoragePersistence.Destroy(world, expired);
        }
        world.SaveChanges();
        var wrecks = world
            .Set<WorldWreck>()
            .AsNoTracking()
            .Where(w => w.SystemId == systemId && w.ExpiresAt > now)
            .OrderBy(w => w.Id)
            .ToArray();
        var occupied = world
            .Set<OwnedItem>()
            .Where(i => wrecks.Select(w => w.Id).Contains(i.LocationId))
            .Select(i => i.LocationId)
            .Distinct()
            .Concat(world.Ships.Where(s => wrecks.Select(w => (long?)w.Id).Contains(s.StoredInId)).Select(s => s.StoredInId!.Value))
            .ToHashSet();
        return wrecks
            .Select(w =>
            {
                var type = catalog.Set<ItemType>().Single(t => t.Id == w.TypeId);
                return new WreckContact(
                    w.Id,
                    w.NpcId ?? w.ShipId!.Value,
                    w.OwnerId,
                    w.CorporationId,
                    w.TypeId,
                    w.Name,
                    w.SystemId,
                    new(w.X, w.Y, w.Z),
                    type.Mass ?? 0,
                    (float)(type.Radius ?? 0),
                    !occupied.Contains(w.Id)
                );
            })
            .ToArray();
    }

    private static (Pilot Pilot, WorldWreck Wreck) Access(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long wreckId,
        long now
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var wreck = world.Set<WorldWreck>().Find(wreckId);
        if (
            wreck is null
            || wreck.ExpiresAt <= now
            || wreck.OwnerId != pilotId
            || pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == pilot.ActiveShip.StationId).SolarSystemId) != wreck.SystemId
        )
        {
            throw new InvalidDataException("The wreck is unavailable or you do not have its loot rights.");
        }
        var hull = catalog.Set<ItemType>().Single(t => t.Id == pilot.ActiveShip.TypeId);
        var type = catalog.Set<ItemType>().Single(t => t.Id == wreck.TypeId);
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

    public WreckInventory Read(long pilotId, long wreckId, long now)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (_, wreck) = Access(world, catalog, pilotId, wreckId, now);
        return new(
            new(
                wreck.Id,
                wreck.TypeId,
                wreck.OwnerId,
                wreck.SystemId,
                (int)InventoryFlag.AutoFit,
                1,
                WreckDefinitions.Group,
                WreckDefinitions.Category,
                PilotInventoryStore.Volume(catalog, wreck.TypeId),
                true
            ),
            PilotInventoryStore.List(world, catalog, pilotId, wreckId)
        );
    }

    public IReadOnlyList<InventoryChange> Take(
        long pilotId,
        long shipId,
        long wreckId,
        IReadOnlyList<long> itemIds,
        int? quantity,
        long now,
        InventoryFlag destinationFlag = InventoryFlag.Cargo
    )
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var (pilot, _) = Access(world, catalog, pilotId, wreckId, now);
        if (
            pilot.ActiveShipId != shipId
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || quantity is <= 0
            || quantity.HasValue && itemIds.Count != 1
        )
        {
            throw new InvalidDataException("Select available wreck contents and a valid quantity for your active ship's cargo.");
        }
        var items = world
            .Set<OwnedItem>()
            .Where(i => itemIds.Contains(i.Id) && i.OwnerId == pilotId && i.LocationId == wreckId)
            .OrderBy(i => i.Id)
            .ToArray();
        var storedShips = world
            .Ships.Where(s => itemIds.Contains(s.Id) && s.OwnerId == pilotId && s.StoredInId == wreckId)
            .OrderBy(s => s.Id)
            .ToArray();
        if (
            items.Length + storedShips.Length != itemIds.Count
            || items.Any(i => i.Quantity <= 0 || quantity > i.Quantity)
            || (storedShips.Length > 0 && quantity is > 1)
        )
        {
            throw new InvalidDataException("The requested wreck contents are no longer available.");
        }
        var hold = ShipHoldRules.Definition((int)destinationFlag);
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, pilot.ActiveShipId, (int)destinationFlag);
        if (
            hold is null
            || (capacity <= 0 && destinationFlag != InventoryFlag.Cargo)
            || items.Any(i => !hold.Allows(PilotInventoryStore.Read(catalog, i)))
            || storedShips.Any(s => !hold.AllowsAssembledShip || !hold.Allows(PilotFleetStore.Read(catalog, s)))
        )
        {
            throw new InvalidDataException("The destination hold cannot accept these wreck contents.");
        }
        if (destinationFlag == InventoryFlag.FrigateEscapeBay)
        {
            throw new InvalidDataException("Load the frigate escape bay while docked.");
        }
        double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, shipId, (int)destinationFlag);
        double added =
            items.Sum(i => PilotInventoryStore.Read(catalog, i).Volume * (quantity ?? i.Quantity))
            + storedShips.Sum(s => PilotInventoryStore.Volume(catalog, s.TypeId, singleton: true));
        if (added > capacity - used + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(Math.Max(0, capacity - used), added);
        }
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var before = PilotInventoryStore.Read(catalog, item);
            int moved = quantity ?? item.Quantity;
            if (moved < item.Quantity)
            {
                item.Quantity -= moved;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                var split = world
                    .Set<OwnedItem>()
                    .Add(
                        new()
                        {
                            Id = InventoryPersistence.NextId(world),
                            OwnerId = pilotId,
                            TypeId = item.TypeId,
                            LocationId = shipId,
                            FlagId = (int)destinationFlag,
                            Quantity = moved,
                        }
                    )
                    .Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, split), null));
            }
            else
            {
                item.LocationId = shipId;
                item.FlagId = (int)destinationFlag;
                item.Online = false;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
        }
        foreach (var stored in storedShips)
        {
            var previous = PilotFleetStore.Read(catalog, stored);
            stored.StoredInId = shipId;
            stored.StorageFlagId = (int)destinationFlag;
            changes.Add(new(PilotFleetStore.Read(catalog, stored), previous));
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }
}
