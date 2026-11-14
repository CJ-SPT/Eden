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
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

[Injectable]
internal sealed partial class WreckStore(WorldDatabase database) : IWrecks
{
    // Called within the fatal weapon transaction: a failed catalog lookup or
    // bounty debit rolls back the wreck and its contents along with the kill.
    internal static void Create(WorldDbContext world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        var ded = Eden.World.Exploration.CombatAnomalyStore.DedLoot(new Eden.World.Runtime.Economy.SqlWorldAction(world), catalog, npc);
        if (npc.Role == NpcRole.MissionStructure && ded is null)
        {
            return;
        }
        var definition = catalog.Read(CatalogData.Client.Types, (long)npc.TypeId);
        var type = definition?.WreckTypeID is long wreckType
            ? (catalog.FittingType(wreckType) is { GroupId: WreckDefinitions.Group } wreckDefinition ? wreckDefinition : null)
            : null;
        bool container = ded is not null && (npc.Role == NpcRole.MissionStructure || type is null);
        if (container)
        {
            type = catalog.RequireItem(JetcanRules.TypeId);
        }
        if (
            type is null
            && definition is { WreckTypeID: null }
            && (
                npc.Role != NpcRole.Pirate
                || catalog.RequireItem(npc.TypeId).GroupId
                    is NpcEncounterDefinitions.SentryGroup
                        or NpcEncounterDefinitions.OverseerSentryGroup
            )
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
                    Name = (npc.Name ?? catalog.RequireItem(npc.TypeId).Name) + " " + type.Name,
                    X = npc.X,
                    Y = npc.Y,
                    Z = npc.Z,
                    CreatedAt = now,
                    ExpiresAt = checked(now + WreckDefinitions.LifetimeSeconds * TimeSpan.TicksPerSecond),
                }
            )
            .Entity;
        if (!container)
        {
            Eden.World.Information.SalvagePersistence.Seed(catalog, wreck);
        }
        if (ded is not null)
        {
            foreach (var reward in ded)
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = pilot.Id,
                        TypeId = reward.TypeId,
                        LocationId = wreck.Id,
                        FlagId = (int)InventoryFlag.Cargo,
                        Quantity = reward.BlueprintCopy ? 1 : reward.Quantity,
                        Singleton = reward.BlueprintCopy,
                        BlueprintRuns = reward.BlueprintCopy ? reward.Quantity : -1,
                    }
                );
            }
            return;
        }
        foreach (var drop in Drops(world, catalog, npc).Concat(Eden.World.Agents.PilotMissionStore.KeyLoot(world, npc, now)))
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
        Eden.World.Information.SalvagePersistence.Seed(catalog, wreck);
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
        var live = LiveWorld.Find(database.Path);
        if (live is null)
        {
            return ListSql(systemId, now, includeJetcans: true);
        }
        IReadOnlyList<WreckContact> contacts = live.Reads?.Wrecks(systemId, now, Load) ?? Load();
        var cans = live.Reads?.Jetcans(systemId, now) ?? JetcanStore.ReadContacts(live, database, systemId, now).Contacts;
        return live.Tractors.Contacts(cans.Count == 0 ? contacts : contacts.Concat(cans).ToArray(), now);

        IReadOnlyList<WreckContact> Load() => live.SqlConnections == 0 ? ListResident(systemId, now) : ListSql(systemId, now);
    }

    private IReadOnlyList<WreckContact> ListResident(int systemId, long now)
    {
        using WorldActionScope scope = new(database, new(systems: [systemId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var world = scope.World;
        var expired = world.Filter<WorldWreck>(w => w.SystemId == systemId && w.ExpiresAt <= now).Select(w => w.Id).ToHashSet();
        if (expired.Count != 0)
        {
            world.RemoveRange(world.Filter<OwnedItem>(i => expired.Contains(i.LocationId)));
            foreach (var ship in world.Filter<Ship>(s => s.StoredInId.HasValue && expired.Contains(s.StoredInId.Value)).ToArray())
            {
                ShipStoragePersistence.Destroy(world, ship);
            }
        }
        var contacts = ReadContacts(world, catalog, systemId, now);
        scope.Commit();
        return contacts;
    }

    private IReadOnlyList<WreckContact> ListSql(int systemId, long now, bool includeJetcans = false)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        // Remove expired jetcans and their contents together. An empty system cache
        // reaches this path at its first read and at the next container deadline.
        if (includeJetcans && world.Set<SavedJetcan>().Any(c => c.SystemId == systemId && c.ExpiresAt <= now))
        {
            using WorldDbContext cleanup = new(database.Path) { Participants = new(systems: [systemId]) };
            using var transaction = cleanup.Database.BeginTransaction();
            JetcanStore.Expire(cleanup, now, systemId);
            transaction.Commit();
        }
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
        var action = new SqlWorldAction(world);
        var contacts = ReadContacts(action, catalog, systemId, now);
        return includeJetcans
            ? contacts
                .Concat(JetcanStore.Contacts(catalog, JetcanStore.List(action, systemId).Where(c => c.ExpiresAt > now).ToArray()))
                .ToArray()
            : contacts;
    }

    private static IReadOnlyList<WreckContact> ReadContacts(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        var wrecks = world.Filter<WorldWreck>(w => w.SystemId == systemId && w.ExpiresAt > now).OrderBy(w => w.Id).ToArray();
        var ids = wrecks.Select(w => w.Id).ToHashSet();
        var storedInIds = ids.Select(id => (long?)id).ToHashSet();
        var occupied = world
            .Filter<OwnedItem>(i => ids.Contains(i.LocationId))
            .Select(i => i.LocationId)
            .AsEnumerable()
            .Concat(world.Filter<Ship>(s => storedInIds.Contains(s.StoredInId)).Select(s => s.StoredInId!.Value))
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
                    !occupied.Contains(w.Id),
                    type.GroupId
                );
            })
            .Concat(Eden.World.Planetary.PlanetaryOrbitStore.Contacts(world, catalog, systemId, now))
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
        using CatalogDbContext catalog = new(database.CatalogPath);
        if (JetcanRules.IsJetcan(wreckId))
        {
            return LiveWorld.Find(database.Path) is { } live
                ? live.Economy.Inspect(world => JetcanStore.Read(world, catalog, pilotId, wreckId, now))
                : WorldActionScope.Inspect(database, world => JetcanStore.Read(world, catalog, pilotId, wreckId, now));
        }
        if (Eden.Gameplay.Planetary.PlanetaryRules.IsOffice(wreckId) || Eden.Gameplay.Planetary.PlanetaryRules.IsLaunch(wreckId))
        {
            var orbit = WorldActionScope.Inspect(
                database,
                world => Eden.World.Planetary.PlanetaryOrbitStore.Read(world, catalog, pilotId, wreckId, now)
            );
            return new(orbit.Container, orbit.Items);
        }
        if (LiveWorld.Find(database.Path) is { } resident)
        {
            return resident.Economy.Inspect(world =>
            {
                var (_, wreck) = Access(world, catalog, pilotId, wreckId, now);
                return new WreckInventory(WreckItem(catalog, wreck), PilotInventoryStore.List(world, catalog, wreck.OwnerId, wreckId));
            });
        }
        using WorldDbContext world = new(database.Path);
        var (_, wreck) = Access(world, catalog, pilotId, wreckId, now);
        return new(WreckItem(catalog, wreck), PilotInventoryStore.List(world, catalog, pilotId, wreckId));
    }

    private static InventoryItem WreckItem(CatalogDbContext catalog, WorldWreck wreck)
    {
        var type = catalog.RequireItem(wreck.TypeId);
        return new(
            wreck.Id,
            wreck.TypeId,
            wreck.OwnerId,
            wreck.SystemId,
            (int)InventoryFlag.AutoFit,
            1,
            type.GroupId,
            type.CategoryId,
            PilotInventoryStore.Volume(catalog, wreck.TypeId),
            true
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
        if (JetcanRules.IsJetcan(wreckId) && LiveWorld.Find(database.Path) is { } live)
        {
            using var action = live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
            using CatalogDbContext residentCatalog = new(database.CatalogPath);
            action.SetAttributes(pilotId, PilotFittingStore.ShipAttributes(action, residentCatalog, pilotId));
            var result = JetcanStore.Take(action, residentCatalog, pilotId, shipId, wreckId, itemIds, quantity, now, destinationFlag);
            action.Commit();
            return result;
        }
        if (Eden.Gameplay.Planetary.PlanetaryRules.IsOffice(wreckId) || Eden.Gameplay.Planetary.PlanetaryRules.IsLaunch(wreckId))
        {
            using WorldActionScope scope = new(database, new(pilots: [pilotId], ships: [shipId]));
            using CatalogDbContext source = new(database.CatalogPath);
            if (!scope.World.Filter<Pilot>(p => p.Id == pilotId && p.ActiveShipId == shipId).Any())
            {
                throw new InvalidDataException("Transfer orbital cargo into your active ship.");
            }
            var result = Eden.World.Planetary.PlanetaryOrbitStore.Move(
                scope.World,
                source,
                pilotId,
                wreckId,
                false,
                new(itemIds, wreckId, (int)destinationFlag, quantity),
                now
            );
            scope.Commit();
            return result;
        }
        if (LiveWorld.Find(database.Path) is { } resident)
        {
            using var action = resident.Economy.Begin(
                new(pilots: [pilotId], ships: [shipId], resources: [new(SharedSimulationResource.Wreck, wreckId)])
            );
            using CatalogDbContext residentCatalog = new(database.CatalogPath);
            var result = Take(action, residentCatalog, pilotId, shipId, wreckId, itemIds, quantity, now, destinationFlag);
            action.Commit();
            return result;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var changes = Take(world, catalog, pilotId, shipId, wreckId, itemIds, quantity, now, destinationFlag);
        transaction.Commit();
        return changes;
    }

    internal static IReadOnlyList<InventoryChange> Take(
        WorldDbContext world,
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
        if (Eden.Gameplay.Planetary.PlanetaryRules.IsOffice(wreckId) || Eden.Gameplay.Planetary.PlanetaryRules.IsLaunch(wreckId))
        {
            if (!world.Pilots.Any(p => p.Id == pilotId && p.ActiveShipId == shipId))
            {
                throw new InvalidDataException("Transfer orbital cargo into your active ship.");
            }
            var orbitChanges = Eden.World.Planetary.PlanetaryOrbitStore.Move(
                world,
                catalog,
                pilotId,
                wreckId,
                false,
                new(itemIds, wreckId, (int)destinationFlag, quantity),
                now
            );
            world.SaveChanges();
            return orbitChanges;
        }
        if (JetcanRules.IsJetcan(wreckId))
        {
            var (owner, can) = JetcanStore.Access(new SqlWorldAction(world), catalog, pilotId, wreckId, now);
            var taken = SpaceLootTransfer.Take(world, catalog, owner, shipId, wreckId, itemIds, quantity, destinationFlag);
            world.SaveChanges();
            if (!world.Set<OwnedItem>().Any(i => i.LocationId == wreckId))
            {
                world.Remove(can);
                world.SaveChanges();
            }
            return taken;
        }
        var (pilot, _) = Access(world, catalog, pilotId, wreckId, now);
        var changes = SpaceLootTransfer.Take(world, catalog, pilot, shipId, wreckId, itemIds, quantity, destinationFlag);
        world.SaveChanges();
        return changes;
    }
}
