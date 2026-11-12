using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Docking;
using Eden.World.Insurance;
using Eden.World.Inventory;
using Eden.World.Loot;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Combat;

internal static partial class ShipLossPersistence
{
    internal static InsurancePayout? Apply(
        WorldAction world,
        CatalogDbContext catalog,
        Pilot pilot,
        long? npcId,
        long shipId,
        int system,
        FlightPosition position,
        long now,
        IShipLootRoll lootRoll,
        CombatParty? playerAttacker = null
    )
    {
        long pilotId = pilot.Id;
        var old = pilot.ActiveShip;
        var moduleIds = world.Set<OwnedItem>().Where(i => i.LocationId == shipId && i.OwnerId == pilotId).Select(i => i.Id).ToArray();
        var items = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && (i.LocationId == shipId || Enumerable.Contains(moduleIds, i.LocationId)))
            .OrderBy(i => i.Id)
            .ToArray();
        var storedShips = world.Ships.Where(s => s.OwnerId == pilotId && s.StoredInId == shipId).OrderBy(s => s.Id).ToArray();
        var storedManifests = storedShips.Select(s => ShipStoragePersistence.Manifest(world, s)).ToArray();
        string storedManifest = JsonSerializer.Serialize(storedManifests);
        string manifest = JsonSerializer.Serialize(items);
        var survivors = items.Where(i => !InventoryDefinitions.IsRigSlot(i.FlagId) && lootRoll.Survives()).Select(i => i.Id).ToHashSet();
        foreach (var stored in storedShips)
        {
            if (lootRoll.Survives())
            {
                survivors.Add(stored.Id);
            }
        }
        var capsuleType =
            (
                catalog.FittingType(FleetDefinitions.CapsuleType) is { GroupId: FleetDefinitions.CapsuleGroup } replacement
                    ? replacement
                    : null
            ) ?? throw new InvalidDataException("The catalog has no supported capsule.");
        var capsule = world
            .Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilotId,
                    TypeId = capsuleType.Id,
                    StationId = old.StationId,
                    Name = capsuleType.Name,
                    Position = new()
                    {
                        SystemId = system,
                        X = position.X,
                        Y = position.Y,
                        Z = position.Z,
                    },
                }
            )
            .Entity;
        world
            .Set<ShipLoss>()
            .Add(
                new()
                {
                    ShipId = shipId,
                    PilotId = pilotId,
                    NpcId = npcId,
                    AttackerPilotId = playerAttacker?.CharacterId,
                    CapsuleId = capsule.Id,
                    TypeId = old.TypeId,
                    SystemId = system,
                    Time = now,
                    Name = old.Name,
                    ItemsJson = manifest,
                    StoredShipsJson = storedManifest,
                    DroppedItemIdsJson = JsonSerializer.Serialize(survivors.Order()),
                    X = position.X,
                    Y = position.Y,
                    Z = position.Z,
                }
            );
        // Snapshot original locations and fitted flags before wreck creation
        // moves surviving inventory into loose cargo.
        var reportItems = PilotInteractionHistoryStore.LostShipItems(shipId, items, storedManifests, survivors);
        PilotInteractionHistoryStore.Destroyed(
            world,
            pilotId,
            $"ship:{shipId}",
            now,
            system,
            PilotInteractionHistoryStore.Player(world, pilot, shipId),
            playerAttacker ?? PilotInteractionHistoryStore.Npc(world.Set<WorldNpc>().Find(npcId!.Value)!),
            reportItems,
            shareWithAttackers: true
        );
        var wreck = WreckStore.CreatePlayer(world, catalog, old, pilot, system, now, items, survivors);
        foreach (var stored in storedShips)
        {
            if (survivors.Contains(stored.Id))
            {
                stored.StoredInId = wreck.Id;
                stored.StorageFlagId = (int)InventoryFlag.Cargo;
            }
            else
            {
                ShipStoragePersistence.Destroy(world, stored);
            }
        }
        PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.ShipLost);
        bool concordLoss = npcId is long sourceNpc && world.Set<WorldNpc>().Find(sourceNpc)?.Role == Eden.Gameplay.Combat.NpcRole.Concord;
        Eden.Gameplay.Insurance.InsurancePayout? insurance = null;
        if (concordLoss)
        {
            PilotInsuranceStore.VoidRemovedHull(world, shipId);
        }
        else
        {
            insurance = PilotInsuranceStore.SettleLoss(world, catalog, old, now);
        }
        if (world.Set<SavedConcordResponse>().Find(shipId) is { CompletedAt: null } response)
        {
            ConcordPersistence.Complete(world, response, now);
        }
        pilot.ActiveShip = capsule;
        pilot.ActiveShipId = capsule.Id;
        if (world.Set<SavedLogoff>().Find(pilotId) is { } logoff)
        {
            logoff.ShipId = capsule.Id;
        }
        // Satisfy the active-ship FK before deleting the old hull. Both saves
        // belong to this transaction: a failed capsule/loss cannot consume it.
        world.SaveChanges();
        world.Remove(old);
        return insurance;
    }

    internal static IEnumerable<long> LossNpcParticipants(WorldAction world, string path, long ship)
    {
        var aggressors = world.Set<NpcAggression>().Where(row => row.TargetShipId == ship).Select(row => row.NpcId);
        var physical = LiveWorld.Find(path)?.CombatForShip(ship)?.Aggressors(ship) ?? [];
        return aggressors.Concat(physical).Concat(ConcordResponders(world, ship));
    }

    private static IEnumerable<long> ConcordResponders(WorldAction world, long shipId)
    {
        if (world.Set<SavedConcordResponse>().Find(shipId) is { CompletedAt: null, CommanderId: long commander } response)
        {
            yield return commander;
            yield return response.FirstCaptainId!.Value;
            yield return response.SecondCaptainId!.Value;
        }
    }
}
