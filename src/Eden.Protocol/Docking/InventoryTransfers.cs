using Eden.Gameplay.Agents;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class InventoryTransfers(
    TimeProvider clock,
    IPilotInventory inventory,
    IPilotFitting fitting,
    IPilotFleet fleet,
    IPilotAmmunition ammunition,
    IWrecks wrecks,
    IPilotMissionContainers missionContainers,
    MagazineWire magazinesWire,
    FittingOperations fittingOperations,
    DeferredRefitPrimes refits,
    ICorporationStorage corporationStorage,
    Eden.Gameplay.Exploration.IPilotHacking? hacking = null,
    Eden.Gameplay.Structures.IPilotStructures? structures = null
)
{
    internal PilotCallResult Move(PilotCallContext context, string service, InventoryMoveRequest request)
    {
        var ids = request.ItemIds;
        long source = request.SourceId;
        int flag = request.FlagId;
        int? quantity = request.Quantity;
        long destination = service == "@hangar" ? context.Pilot.DockedLocationId : context.Pilot.ShipId;
        try
        {
            if (
                structures is not null
                && source >= Eden.Gameplay.Structures.StructureRules.FirstStructureId
                && structures.Read(context.Pilot.PilotId, source) is not null
                && ids.Count == 1
                && !inventory.List(context.Pilot.PilotId, source).Any(i => i.Id == ids[0])
                && fleet.Find(context.Pilot.PilotId, ids[0]) is null
            )
            {
                var contents = structures.Inventory(context.Pilot.PilotId, source);
                if (contents.SingleOrDefault(i => i.Id == ids[0]) is { } owned)
                {
                    if (flag != (int)(service == "@hangar" ? InventoryFlag.Hangar : InventoryFlag.Cargo))
                    {
                        throw new InvalidDataException("Withdraw structure contents into personal hangar or ordinary cargo.");
                    }
                    Eden.Protocol.Structures.StructureHandler.Notify(
                        context,
                        structures.Withdraw(context.Pilot.PilotId, source, owned.Id, quantity ?? owned.Quantity, service != "@hangar")
                    );
                    return PilotCallResult.None;
                }
            }
            if (source is >= CorporationStorageRules.FirstOfficeId and <= CorporationStorageRules.LastOfficeId)
            {
                CorporationStorageHandler.NotifyItems(context, corporationStorage.Move(context.Pilot.PilotId, destination, request));
                return PilotCallResult.None;
            }
            var previousBanks = fittingOperations.ReadBanks(context);
            var previousMagazines = ammunition.Read(context.Pilot.PilotId, context.Pilot.ShipId);
            bool looting =
                context.Pilot.Position is not null
                && destination == context.Pilot.ShipId
                && ShipHoldRules.IsHold(flag)
                && (
                    context.Flight.VisibleWrecks.Any(w => w.Id == source)
                    || MissionContainerRules.IsContainer(source)
                    || Eden.Gameplay.Exploration.HackingRules.IsContainer(source)
                );
            if (looting && !context.Flight.CanTarget)
            {
                return new PilotCallResult.Failure("Container contents cannot be transferred during warp or a system transition.");
            }
            if (looting)
            {
                context.Flight.Checkpoint();
                if (!context.Flight.CanTarget)
                {
                    return new PilotCallResult.Failure("Container contents cannot be transferred during warp or a system transition.");
                }
            }
            var changes = TransferItems(context, request, destination, looting);
            var currentMagazines = ammunition.Read(context.Pilot.PilotId, context.Pilot.ShipId).ToDictionary(m => m.ModuleId);
            var changedMagazines = previousMagazines
                .Where(m => !currentMagazines.TryGetValue(m.ModuleId, out var current) || m != current)
                .ToArray();
            // Charge sublocations are keyed by ship/slot/type in this client.
            // Clear every old slot before moving either module in a swap, then
            // prime the new slots after their inventory rows have moved.
            foreach (var magazine in changedMagazines)
            {
                MagazineWire.NotifyQuantity(context, magazine, 0, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            var relocated = changes
                .Where(c =>
                    c.Previous is { } old
                    && old.LocationId == c.Item.LocationId
                    && old.FlagId != c.Item.FlagId
                    && InventoryDefinitions.IsFittedSlot(old.FlagId)
                    && InventoryDefinitions.IsFittedSlot(c.Item.FlagId)
                )
                .ToDictionary(c => c.Item.Id, c => c.Previous! with { FlagId = (int)InventoryFlag.Cargo });
            // A direct fitted-to-fitted flag change updates dogmaItem.flagID,
            // but build 3662168 retains its old ship slot/effect associations.
            // Detach all moving modules from the client rack before refitting
            // either one. This is cache staging, never a saved cargo transfer.
            foreach (var change in changes.Where(c => relocated.ContainsKey(c.Item.Id)))
            {
                InventoryChangesWire.NotifyItemChange(context, new(relocated[change.Item.Id], change.Previous));
            }
            foreach (var change in changes)
            {
                InventoryChangesWire.NotifyItemChange(
                    context,
                    relocated.TryGetValue(change.Item.Id, out var detached) ? change with { Previous = detached } : change
                );
            }
            if (looting)
            {
                context.Flight.RefreshWrecks();
            }
            if (
                changes.Any(c =>
                    InventoryDefinitions.IsFittedSlot(c.Item.FlagId)
                    || (c.Previous is not null && InventoryDefinitions.IsFittedSlot(c.Previous.FlagId))
                )
            )
            {
                // Let OnItemChange fetch the module through ItemGetInfo. A live
                // OnGodmaPrimeItem replaces its inventory row with an incompatible
                // godma row in build 3662168, removing it from clientDogmaLocation.
                fittingOperations.NotifyOnlineChanges(context, changes);
                fittingOperations.NotifyShipAttributes(context, previousBanks);
            }
            var movedMagazines = changedMagazines
                .Where(m => currentMagazines.ContainsKey(m.ModuleId))
                .Select(m => new MagazineChange(null, currentMagazines[m.ModuleId]))
                .ToArray();
            if (relocated.Count > 0 && movedMagazines.Length > 0)
            {
                // Refitting fetches ItemGetInfo asynchronously. A charge fitted
                // before those replies can start its passive effects with no
                // module in the new slot. Wait for the fresh module primes.
                refits.Defer(context.Pilot.ShipId, relocated.Keys, movedMagazines);
            }
            else
            {
                magazinesWire.NotifyChanges(context, movedMagazines);
            }
            return PilotCallResult.None;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallResult.NamedFailure("NotEnoughCargoSpace", new CargoSpaceErrorValues(error.Available, error.Required));
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private IReadOnlyList<InventoryChange> TransferItems(
        PilotCallContext context,
        InventoryMoveRequest request,
        long destination,
        bool looting
    )
    {
        if (looting)
        {
            if (Eden.Gameplay.Exploration.HackingRules.IsContainer(request.SourceId))
            {
                return hacking?.Take(context.Pilot.PilotId, context.Pilot.ShipId, request, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc())
                    ?? throw new InvalidDataException("Exploration transfers are unavailable.");
            }
            return MissionContainerRules.IsContainer(request.SourceId)
                ? missionContainers.Take(context.Pilot.PilotId, context.Pilot.ShipId, request)
                : LootWreck(context, request);
        }
        if (
            destination == context.Pilot.ShipId
            && (request.FlagId == (int)InventoryFlag.AutoFit || InventoryDefinitions.IsFittedSlot(request.FlagId))
        )
        {
            return FitItems(context, request);
        }
        return MoveInventory(context, request, destination);
    }

    private IReadOnlyList<InventoryChange> LootWreck(PilotCallContext context, InventoryMoveRequest request) =>
        wrecks.Take(
            context.Pilot.PilotId,
            context.Pilot.ShipId,
            request.SourceId,
            request.ItemIds,
            request.Quantity,
            clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
            (InventoryFlag)request.FlagId
        );

    private IReadOnlyList<InventoryChange> FitItems(PilotCallContext context, InventoryMoveRequest request) =>
        fitting.Fit(context.Pilot.PilotId, request.ItemIds, request.SourceId, request.FlagId);

    private IReadOnlyList<InventoryChange> MoveInventory(PilotCallContext context, InventoryMoveRequest request, long destination) =>
        inventory.Move(context.Pilot.PilotId, destination, request);
}
