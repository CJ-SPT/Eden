using System.Globalization;
using System.Security.Cryptography;
using Eden.Data.Catalog;
using Eden.Data.World.Agents;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Agents;
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
        if (JetcanRules.IsJetcan(wreckId))
        {
            return JetcanStore.Take(world, catalog, pilotId, shipId, wreckId, itemIds, quantity, now, destinationFlag);
        }
        var (pilot, wreck) = Access(world, catalog, pilotId, wreckId, now);
        bool theft = SpaceLootRights.CheckTake(
            world,
            catalog,
            pilot,
            wreck.SystemId,
            new(wreck.OwnerId, wreck.CorporationId, wreck.Abandoned)
        );
        var changes = SpaceLootTransfer.Take(world, catalog, pilot, shipId, wreckId, itemIds, quantity, destinationFlag, wreck.OwnerId);
        if (theft)
        {
            SpaceLootRights.RecordTheft(world, pilot, wreck.SystemId, now);
        }
        world.SaveChanges();
        return changes;
    }

    private static (Pilot Pilot, WorldWreck Wreck) Access(WorldAction world, CatalogDbContext catalog, long pilotId, long wreckId, long now)
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var wreck = world.Set<WorldWreck>().Find(wreckId);
        if (
            wreck is null
            || wreck.ExpiresAt <= now
            || pilot.ActiveShip.Position is not { } position
            || (position.SystemId ?? catalog.RequireStation(pilot.ActiveShip.StationId).SolarSystemId) != wreck.SystemId
        )
        {
            throw new InvalidDataException("The wreck is unavailable.");
        }
        world.RequireUncloaked(pilot.ActiveShipId);
        var hull = catalog.RequireItem(pilot.ActiveShip.TypeId);
        var type = catalog.RequireItem(wreck.TypeId);
        if (
            new SpacePosition(position.X, position.Y, position.Z).DistanceTo(
                LiveWorld.Find(world.WorldPath)?.Tractors.Position(wreck.Id) ?? new(wreck.X, wreck.Y, wreck.Z)
            )
                - (hull.Radius ?? 0)
                - (type.Radius ?? 0)
            > WreckDefinitions.TransferRange
        )
        {
            throw new InvalidDataException("Approach within 2,500 m of the wreck to transfer its contents.");
        }
        return (pilot, wreck);
    }

    internal static void Create(WorldAction world, CatalogDbContext catalog, WorldNpc npc, Pilot pilot, long now)
    {
        var ded = Eden.World.Exploration.CombatAnomalyStore.DedLoot(world, catalog, npc);
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
        if (
            npc.AnchorKind == NpcAnchorKind.Mission
            && world.Set<SavedMission>().Find(npc.EncounterId - MissionRules.FirstSiteId) is { } mission
        )
        {
            pilot = world.Pilots.Find(mission.PilotId) ?? pilot;
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

    private static IReadOnlyList<NpcLootDrop> Drops(WorldAction world, CatalogDbContext catalog, WorldNpc npc)
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
        WorldAction world,
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
        world.RemoveRange(world.Set<ModuleCooldown>().Where(c => Enumerable.Contains(ids, c.ModuleId)));
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
}
