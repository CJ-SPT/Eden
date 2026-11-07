using System.Text.Json;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

[Injectable]
internal sealed class PilotLossSettlement(PilotTrainingStore training, IShipLootRoll lootRoll)
{
    internal PilotLossResult Apply(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        long shipId,
        int system,
        FlightPosition position,
        long now,
        CombatParty attacker
    )
    {
        var old = pilot.ActiveShip;
        if (old.Id != shipId || old.OwnerId != pilot.Id || old.Position is null || world.Set<ShipLoss>().Any(l => l.ShipId == shipId))
        {
            throw new InvalidDataException("The destroyed ship is no longer the pilot's active ship.");
        }
        if (catalog.RequireItem(old.TypeId).GroupId != FleetDefinitions.CapsuleGroup)
        {
            return new(ShipLossPersistence.Apply(world, catalog, pilot, null, shipId, system, position, now, lootRoll, attacker), []);
        }
        var home = PilotHomeStationStore.Read(world, catalog, pilot);
        var previous = PilotFleetStore.Read(catalog, old);
        var type = catalog.RequireItem(FleetDefinitions.CapsuleType);
        if (type.GroupId != FleetDefinitions.CapsuleGroup)
        {
            throw new InvalidDataException("The catalog has no supported replacement capsule.");
        }
        var items = ShipStoragePersistence.Contents(world, pilot.Id, shipId);
        var implants = (
            from implant in world.Set<SavedPilotImplant>()
            join body in world.Set<SavedJumpClone>() on implant.CloneId equals body.Id
            where body.PilotId == pilot.Id && body.Active
            orderby implant.Slot
            select implant
        ).ToArray();
        var lostItems = PilotInteractionHistoryStore
            .LostItems(shipId, items, new HashSet<long>())
            .Concat(implants.Select(i => new CombatLostItem(i.TypeId, (int)InventoryFlag.Implant, true, 0, 1, [])))
            .ToArray();
        var replacement = world
            .Ships.Add(
                new Ship
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = pilot.Id,
                    TypeId = type.Id,
                    StationId = home.Id,
                    Name = type.Name,
                }
            )
            .Entity;
        world.Add(
            new ShipLoss
            {
                ShipId = shipId,
                PilotId = pilot.Id,
                AttackerPilotId = attacker.CharacterId,
                CapsuleId = replacement.Id,
                TypeId = old.TypeId,
                SystemId = system,
                Time = now,
                Name = old.Name,
                ItemsJson = JsonSerializer.Serialize(items),
                StoredShipsJson = "[]",
                DroppedItemIdsJson = "[]",
                X = position.X,
                Y = position.Y,
                Z = position.Z,
            }
        );
        PilotInteractionHistoryStore.Destroyed(
            world,
            pilot.Id,
            $"ship:{shipId}",
            now,
            system,
            PilotInteractionHistoryStore.Player(world, pilot, shipId),
            attacker,
            lostItems,
            shareWithAttackers: true
        );
        world.Add(
            new SavedHomeCloneTransfer
            {
                PilotId = pilot.Id,
                OldShipId = shipId,
                NewShipId = replacement.Id,
                SourceStationId = old.StationId,
                DestinationStationId = home.Id,
                Time = now,
            }
        );
        PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.PodLost);
        training.ChangeAttributes(world, pilot.Id, () => PilotJumpCloneStore.ResetActive(world, pilot.Id), effectiveTime: now);
        pilot.ActiveShip = replacement;
        pilot.ActiveShipId = replacement.Id;
        world.RemoveRange(world.Set<SavedLogoff>().Where(l => l.PilotId == pilot.Id));
        // Release the active-ship FK before removing the dead capsule. The caller
        // owns one transaction for damage, body/implant loss, history and respawn.
        world.SaveChanges();
        ShipStoragePersistence.Destroy(world, old);
        return new(null, [new(previous with { Quantity = 0 }, previous), new(PilotFleetStore.Read(catalog, replacement), null)]);
    }
}
