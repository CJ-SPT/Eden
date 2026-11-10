using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Corporations;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable(InjectionType.Scoped)]
internal sealed class InventoryHandler(
    IPilotInventory inventory,
    IPilotFitting fitting,
    IPilotFleet fleet,
    IPilotAmmunition ammunition,
    MagazineWire magazinesWire,
    ShipDogmaBuilder dogma,
    InventoryTransfers transfers,
    FittingOperations fittingOperations,
    DeferredRefitPrimes refits,
    IPilotContracts contracts,
    IPilotInventoryStacks stacks,
    ICorporationStorage corporationStorage
) : IPilotCallHandler, ISelectedPilotCallHandler
{
    private const string ShipInventoryPrefix = "@ship:";

    internal static BlueConstructor ItemDescriptor() => InventoryWire.Descriptor();

    private static InventoryRow Item(InventoryItem item) => InventoryRow.From(item);

    private BlueList<InventoryRow> List(DockedPilot pilot, string service, long? flag)
    {
        List<InventoryRow> result = [];
        if (service == "@hangar" && pilot.Position is not null)
        {
            return new(result);
        }
        long location = service == "@hangar" ? pilot.DockedLocationId : ShipContainer(service, pilot);
        if (service == "@hangar" && (flag is null or (long)InventoryFlag.Hangar))
        {
            result.AddRange(fleet.Ships(pilot.PilotId, pilot.DockedLocationId).Select(Item));
        }
        result.AddRange(inventory.List(pilot.PilotId, location).Where(i => flag is null || i.FlagId == flag).Select(Item));
        return new(result);
    }

    private static long ShipContainer(string service, DockedPilot pilot) =>
        service == "@ship" ? pilot.ShipId
        : service.StartsWith(ShipInventoryPrefix, StringComparison.Ordinal)
        && long.TryParse(service.AsSpan(ShipInventoryPrefix.Length), out long id)
            ? id
        : 0;

    private bool CanReadShip(PilotCallContext context, InventoryItem ship)
    {
        var pilot = context.Pilot;
        var parent = ShipHoldRules.Definition(ship.FlagId)?.AllowsAssembledShip == true ? fleet.Find(pilot.PilotId, ship.LocationId) : null;
        var wreck = context.Flight.CanTarget ? context.Flight.VisibleWrecks.FirstOrDefault(w => w.Id == ship.LocationId) : null;
        return ShipStorageRules.CanRead(
            new(pilot.PilotId, pilot.ShipId, pilot.DockedLocationId, pilot.Position is null),
            ship,
            parent,
            wreck is not null && pilot.Position is { } position ? new(wreck, position, pilot.Flight.ShipRadius) : null
        );
    }

    private PilotCallResult MoveFromWire(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords
    )
    {
        if (args.Length != 2 || args[1] is not long source)
        {
            return PilotCallResult.Unsupported;
        }
        long[] ids;
        if (method == "Add" && args[0] is long id)
        {
            ids = [id];
        }
        else if (method == "MultiAdd" && args[0] is List<object?> list && list.All(i => i is long))
        {
            ids = list.Cast<long>().ToArray();
        }
        else
        {
            return PilotCallResult.Unsupported;
        }
        object? flagValue = keywords?.GetValueOrDefault("flag");
        object? quantityValue = keywords?.GetValueOrDefault("qty");
        if (
            (flagValue is not null && flagValue is not long)
            || (quantityValue is not null && quantityValue is not long)
            || (flagValue is long flagNumber && (flagNumber < 0 || flagNumber > int.MaxValue))
            || (quantityValue is long quantityNumber && (quantityNumber <= 0 || quantityNumber > int.MaxValue))
        )
        {
            return new PilotCallResult.Failure("Invalid cargo flag or quantity.");
        }
        int flag =
            flagValue is long f ? (int)f
            : service == "@hangar" ? (int)InventoryFlag.Hangar
            : (int)InventoryFlag.Cargo;
        int? quantity = quantityValue is long q ? (int)q : null;
        return transfers.Move(context, service, new InventoryMoveRequest(ids, source, flag, quantity));
    }

    private PilotCallResult ItemInfo(PilotCallContext context, long id)
    {
        var pilot = context.Pilot;
        if (id == pilot.ShipId)
        {
            return dogma.PrimeShip(pilot);
        }
        if (id == pilot.PilotId)
        {
            return dogma.PrimePilot(pilot);
        }
        var item = inventory.List(pilot.PilotId, pilot.ShipId).FirstOrDefault(i => i.Id == id);
        if (item is null && pilot.Position is null)
        {
            item = inventory.List(pilot.PilotId, pilot.DockedLocationId).FirstOrDefault(i => i.Id == id);
        }
        if (item is null && pilot.Position is not null && context.Flight.VisibleDrones.Any(d => d.Id == id && d.PilotId == pilot.PilotId))
        {
            item = inventory.List(pilot.PilotId, pilot.SystemId).FirstOrDefault(i => i.Id == id);
        }
        if (
            item is null
            || (
                !InventoryDefinitions.IsFittedSlot(item.FlagId)
                && !AmmunitionDefinitions.IsCrystalGroup(item.GroupId)
                && item.CategoryId != (int)InventoryCategory.Drone
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        var result = dogma.PrimeModule(pilot, item);
        if (InventoryDefinitions.IsFittedSlot(item.FlagId))
        {
            refits.Primed(context, id);
        }
        return result;
    }

    private PilotCallResult SplitFromWire(PilotCallContext context, object?[] args, Dictionary<string, object?>? keywords)
    {
        var pilot = context.Pilot;
        // Build 3662168 invItemFunctions.SplitStack calls the inventory manager with the
        // physical station, item ID, chosen quantity and owner ID (not a corporation flag).
        if (
            args is not [long station, long itemId, long quantity, long owner]
            || pilot.Position is not null
            || station != pilot.DockedLocationId
            || owner != pilot.PilotId && owner != pilot.CorpId
            || quantity is <= 0 or > int.MaxValue
            || keywords is { Count: > 0 }
        )
        {
            return new PilotCallResult.Failure("Choose an owned stack at your current station and a valid quantity.");
        }
        if (owner != pilot.PilotId)
        {
            try
            {
                var office =
                    corporationStorage.Offices(pilot.PilotId).SingleOrDefault(o => o.StationId == station)
                    ?? throw new InvalidDataException("Your corporation has no office here.");
                var shared =
                    corporationStorage.List(pilot.PilotId, office.Id, null).SingleOrDefault(i => i.Id == itemId)
                    ?? throw new InvalidDataException("This stack is unavailable in your corporation hangars.");
                InventoryStackRules.Split(shared, (int)quantity);
                CorporationStorageHandler.NotifyItems(
                    context,
                    corporationStorage.Move(pilot.PilotId, office.Id, new([itemId], office.Id, shared.FlagId, (int)quantity))
                );
                return PilotCallResult.None;
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
        }
        var item = inventory.List(owner, station).Concat(inventory.List(owner, pilot.ShipId)).FirstOrDefault(item => item.Id == itemId);
        string? destination = item switch
        {
            { LocationId: var location, FlagId: (int)InventoryFlag.Hangar } when location == station => "@hangar",
            { LocationId: var location, FlagId: var flag } when location == pilot.ShipId && ShipHoldRules.IsHold(flag) => "@ship",
            _ => null,
        };
        return destination is null || item is null
            ? new PilotCallResult.Failure("The stack is unavailable at your current station.")
            : transfers.Move(context, destination, new([itemId], item.LocationId, item.FlagId, (int)quantity));
    }

    private PilotCallResult MergeFromWire(PilotCallContext context, long destination, object?[] args, Dictionary<string, object?>? keywords)
    {
        try
        {
            // Build 3662168 invCacheContainer.MultiMerge sends a list of
            // (source item ID, retained target item ID, quantity) tuples and the source container.
            if (args is not [List<object?> rows, long source] || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Choose source stacks, destination stacks and quantities to merge.");
            }
            var operations = rows.Select(row =>
                {
                    var tuple = BlueRead.Tuple(row, 3);
                    return tuple is [long sourceId, long targetId, long quantity] && quantity is > 0 and <= int.MaxValue
                        ? new InventoryStackMerge(sourceId, targetId, (int)quantity)
                        : throw new InvalidDataException("Invalid stack merge operation.");
                })
                .ToArray();
            foreach (var change in stacks.Merge(context.Pilot.PilotId, source, destination, operations))
            {
                InventoryChangesWire.NotifyItemChange(context, change);
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
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The combined stack exceeds the supported quantity.");
        }
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object? arguments,
        Dictionary<string, object?>? keywords
    ) => (service, method) == ("invbroker", "GetItemDescriptor") ? ItemDescriptor() : PilotCallResult.Unsupported;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        var pilot = context.Pilot;
        long container = ShipContainer(service, pilot);
        if (method == "MultiMerge" && (service == "@hangar" || container != 0))
        {
            return MergeFromWire(context, service == "@hangar" ? pilot.DockedLocationId : container, args, keywords);
        }
        if (method == "StackAll" && (service == "@hangar" || container != 0))
        {
            try
            {
                if (
                    args.Length > 1
                    || args.Length == 1 && args[0] is not long
                    || keywords is { Count: > 0 } && keywords.Keys.Any(key => key != "flag")
                    || args.Length == 1 && keywords?.ContainsKey("flag") == true
                )
                {
                    throw new InvalidDataException("Choose one inventory flag to stack.");
                }
                object? flagValue = args.Length == 1 ? args[0] : keywords?.GetValueOrDefault("flag");
                int flag =
                    flagValue is null ? (int)(service == "@hangar" ? InventoryFlag.Hangar : InventoryFlag.Cargo)
                    : flagValue is long value && value is >= 0 and <= int.MaxValue ? (int)value
                    : throw new InvalidDataException("Invalid inventory flag.");
                foreach (var change in stacks.StackAll(pilot.PilotId, service == "@hangar" ? pilot.DockedLocationId : container, flag))
                {
                    InventoryChangesWire.NotifyItemChange(context, change);
                }
                return PilotCallResult.None;
            }
            catch (InvalidDataException error)
            {
                return new PilotCallResult.Failure(error.Message);
            }
            catch (OverflowException)
            {
                return new PilotCallResult.Failure("The combined stack exceeds the supported quantity.");
            }
        }
        if (container != 0)
        {
            if (context.DestroyedShip is { } lost && lost.ShipId == container)
            {
                // During capsule adoption, holdTracker.OnCapacityChange can
                // rebind the old cargo after invCache has discarded it. Supply
                // an empty read for this session's just-lost hull, never assets
                // or a writable inventory. Departure/boarding expires it.
                return method switch
                {
                    "List" or "ListByFlags" => BlueEmpty.List,
                    "GetSelfInvItem" => Item(
                        new InventoryItem(
                            lost.ShipId,
                            lost.ShipTypeId,
                            lost.PilotId,
                            (long)InventoryLocation.Graveyard,
                            (int)InventoryFlag.AutoFit,
                            1,
                            lost.ShipGroupId,
                            lost.ShipCategoryId,
                            0,
                            true
                        )
                    ),
                    _ => new PilotCallResult.Failure("The ship was destroyed and its inventory is read-only."),
                };
            }
            var ship = fleet.Find(pilot.PilotId, container);
            if (ship is null || !CanReadShip(context, ship))
            {
                return new PilotCallResult.Failure("The ship inventory is unavailable at your current location.");
            }
            if (method == "List")
            {
                return List(pilot, service, args.FirstOrDefault() as long?);
            }
            if (
                method == "ListByFlags"
                && args.Length == 0
                && keywords?.GetValueOrDefault("flags") is List<object?> flags
                && flags.All(f => f is long)
            )
            {
                return new BlueList<InventoryRow>(
                    inventory.List(pilot.PilotId, container).Where(i => flags.Contains((long)i.FlagId)).Select(Item).ToArray()
                );
            }
            if (method == "GetSelfInvItem")
            {
                return container == pilot.ShipId ? ShipDogmaBuilder.Item(pilot, "ship") : Item(ship);
            }
            if (method == "GetAvailableTurretSlots")
            {
                return checked((int)fitting.Attributes(ship.TypeId).GetValueOrDefault(DogmaAttributeId.TurretSlotsLeft));
            }
            if (method is "Add" or "MultiAdd")
            {
                return container == pilot.ShipId
                    ? MoveFromWire(context, "@ship", method, args, keywords)
                    : new PilotCallResult.Failure("Activate the ship before changing its cargo or fitting.");
            }
        }
        return (service, method) switch
        {
            ("invbroker", "SplitStack") => SplitFromWire(context, args, keywords),
            ("corpStationMgr", "DoStandingCheckForStationService") when pilot.Position is null && args is [(long)StationService.Fitting] =>
                PilotCallResult.None,
            ("corpFittingMgr", "GetCommunityFittings" or "GetFittings") => BlueEmpty.IndexedDictionary,
            ("itemLocking", "GetItemsByLocation") when args is [long location] && location == pilot.LocationId =>
                BlueEmpty.IndexedDictionary,
            ("invbroker", "GetInventory") when pilot.Position is null && args is [10004L, ..] => context.Reference(
                "@hangar",
                PilotBindingLifetime.Location
            ),
            ("invbroker", "GetInventoryFromId")
                when args is [long id, ..] && fleet.Find(pilot.PilotId, id) is { } ship && CanReadShip(context, ship) => context.Reference(
                $"{ShipInventoryPrefix}{id}",
                PilotBindingLifetime.Location
            ),
            ("invbroker", "GetInventoryFromId") when args is [long id, ..] && context.DestroyedShip?.ShipId == id => context.Reference(
                $"{ShipInventoryPrefix}{id}",
                PilotBindingLifetime.Location
            ),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.PilotId) => context.Reference(
                "@pilot",
                PilotBindingLifetime.Pilot
            ),
            ("invbroker", "GetInventoryFromId")
                when args is [long id, ..]
                    && contracts.Package(pilot.PilotId, id) is { } package
                    && (
                        (
                            pilot.Position is null
                            && package.Item.LocationId == pilot.DockedLocationId
                            && package.Item.FlagId == (int)InventoryFlag.Hangar
                        ) || (package.Item.LocationId == pilot.ShipId && package.Item.FlagId == (int)InventoryFlag.Cargo)
                    ) => context.Reference(
                $"{Eden.Protocol.Contracts.CourierPackageHandler.InventoryPrefix}{id}",
                PilotBindingLifetime.Location
            ),
            ("@hangar" or "@ship", "List") => List(pilot, service, args.FirstOrDefault() as long?),
            ("@pilot", "List") => BlueEmpty.List,
            ("@hangar" or "@ship", "Add" or "MultiAdd") => MoveFromWire(context, service, method, args, keywords),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => ShipDogmaBuilder.Item(pilot, service[1..]),
            ("dogmaIM", "GetAllInfo") => dogma.AllInfo(pilot),
            ("dogmaIM", "GetLayerDamageValuesByItems") when args is [HashSet<long> ids] => dogma.DroneBayDamage(pilot, ids),
            ("dogmaIM", "ItemGetInfo") when args is [long id] => ItemInfo(context, id),
            ("dogmaIM", "ItemGetInfo")
                when args is [object?[] { Length: 3 } key] && key is [long hull, long slot, long type] && hull == pilot.ShipId => ammunition
                .Read(pilot.PilotId, hull)
                .FirstOrDefault(m => m.FlagId == slot && m.TypeId == type)
                is { } magazine
                ? magazinesWire.Prime(context.Pilot.PilotId, magazine)
                : PilotCallResult.None,
            ("dogmaIM", "SetModuleOnline" or "TakeModuleOffline") when args is [long ship, long module] => fittingOperations.SetOnline(
                context,
                ship,
                module,
                method == "SetModuleOnline"
            ),
            _ => PilotCallResult.Unsupported,
        };
    }
}
