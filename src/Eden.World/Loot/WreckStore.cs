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
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

[Injectable]
internal sealed class WreckStore(WorldDatabase database) : IWrecks
{
    // Called within the fatal weapon transaction: a failed catalog lookup or
    // bounty debit rolls back the wreck and its contents along with the kill.
    internal static void Create(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        var definition = catalog.Read(CatalogData.Client.Types, (long)npc.TypeId);
        var type = definition?.WreckTypeID is long wreckType
            ? (catalog.FittingType(wreckType) is { GroupId: WreckDefinitions.Group } wreckDefinition ? wreckDefinition : null)
            : null;
        if (
            type is null
            && definition is { WreckTypeID: null }
            && (npc.Role != NpcRole.Pirate || catalog.RequireItem(npc.TypeId).GroupId == NpcEncounterDefinitions.SentryGroup)
        )
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
                    Name = catalog.RequireItem(npc.TypeId).Name + " " + type.Name,
                    X = npc.X,
                    Y = npc.Y,
                    Z = npc.Z,
                    CreatedAt = now,
                    ExpiresAt = checked(now + WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond),
                }
            )
            .Entity;
        foreach (var drop in Drops(world, catalog, npc))
        {
            _ = catalog.RequireItem(drop.TypeId);
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

    private static IReadOnlyList<NpcLootDrop> Drops(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc)
    {
        if (NpcPopulationRules.IsMining(npc.Role))
        {
            var cargo = world.Set<WorldNpcMiningCargo>().Where(c => c.NpcId == npc.Id && c.Quantity > 0).ToArray();
            var result = cargo.Select(c => new NpcLootDrop(c.TypeId, checked((int)c.Quantity))).ToList();
            foreach (var stack in cargo)
            {
                stack.Quantity = 0;
            }
            // The guide permits empty haulers but supplies no drop probability.
            // Eden uses a stable 50% identity roll; no reroll on retry/restart.
            if (npc.Role == NpcRole.MiningHauler && NpcSpawnRules.Seed(npc.SystemId, npc.Id) % 2 == 0)
            {
                string name = catalog.NpcCorporationName(npc.CorporationId)! + " Strong Box";
                if (catalog.Definitions.Items.Values.SingleOrDefault(type => type.Name == name) is { } box)
                {
                    result.Add(new(box.Id, 1));
                }
            }
            return result;
        }
        if (npc.Role != NpcRole.Pirate)
        {
            return [];
        }
        // Retained solely for the injected Rookie encounter fixture. Normal
        // populations retire this type and never select it from asteroid pools.
        if (
            npc.TypeId == NpcEncounterDefinitions.GuristasRookieType
            && world.Set<SavedWorldEconomy>().Find(Eden.Gameplay.Market.WorldEconomySettings.SingletonId)!.TestingMarket
        )
        {
            return [new(WreckDefinitions.StarterLootType, WreckDefinitions.StarterLootQuantity)];
        }
        int groupId = catalog.RequireItem(npc.TypeId).GroupId;
        var group = NpcPirateGroups.All.Concat(NpcPirateGroups.EncounterLoot).SingleOrDefault(g => g.GroupId == groupId);
        if (group?.Commander == true)
        {
            return NpcCommanderLootRules.Roll(group.Faction, NpcSpawnRules.Seed(npc.SystemId, npc.Id));
        }
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
        var definition = catalog.Read(CatalogData.Client.Types, (long)ship.TypeId);
        var type = definition?.WreckTypeID is long wreckType
            ? (catalog.FittingType(wreckType) is { GroupId: WreckDefinitions.Group } wreckDefinition ? wreckDefinition : null)
            : null;
        if (type is null)
        {
            throw new InvalidDataException("The lost ship has no supported catalog wreck.");
        }
        var position = ship.Position ?? throw new InvalidDataException("A destroyed ship must be in space.");
        NaniteRepairPersistence.BeforeShipLoss(world, catalog, ship.Id, now);
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
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("read.wrecks");
        return LiveReadModels.Find(database.Path) is { } resident
            ? resident.Wrecks(systemId, now, () => ListSql(systemId, now))
            : ListSql(systemId, now);
    }

    private IReadOnlyList<WreckContact> ListSql(int systemId, long now)
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
                var type = catalog.RequireItem(w.TypeId);
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
