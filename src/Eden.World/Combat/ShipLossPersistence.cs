using System.Text.Json;
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
using Eden.World.Storage;

namespace Eden.World.Combat;

internal static class ShipLossPersistence
{
    internal static InsurancePayout? Apply(
        WorldDbContext world,
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
            .Where(i => i.OwnerId == pilotId && (i.LocationId == shipId || moduleIds.Contains(i.LocationId)))
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
            catalog.Set<ItemType>().SingleOrDefault(t => t.Id == FleetDefinitions.CapsuleType && t.GroupId == FleetDefinitions.CapsuleGroup)
            ?? throw new InvalidDataException("The catalog has no supported capsule.");
        var capsule = world
            .Ships.Add(
                new()
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
            playerAttacker ?? PilotInteractionHistoryStore.Npc(world.Set<WorldNpc>().Find(npcId)!),
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
        var insurance = PilotInsuranceStore.SettleLoss(world, catalog, old, now);
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
}
