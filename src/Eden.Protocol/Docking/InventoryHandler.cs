using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Ammunition;
using Eden.Protocol.Combat;
using Eden.Protocol.Loot;
using Eden.Protocol.Mining;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Propulsion;
using Eden.Protocol.Repair;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;

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
    DeferredRefitPrimes refits
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
        long location = service == "@hangar" ? pilot.StationId : ShipContainer(service, pilot);
        if (service == "@hangar" && (flag is null or (long)InventoryFlag.Hangar))
        {
            result.AddRange(fleet.Ships(pilot.PilotId, pilot.StationId).Select(Item));
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
        var module = inventory
            .List(pilot.PilotId, pilot.ShipId)
            .FirstOrDefault(i => i.Id == id && InventoryDefinitions.IsFittedSlot(i.FlagId));
        if (module is null)
        {
            return PilotCallResult.Unsupported;
        }
        var result = dogma.PrimeModule(pilot, module);
        refits.Primed(context, id);
        return result;
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
            if (ship is null || (container != pilot.ShipId && (pilot.Position is not null || ship.LocationId != pilot.StationId)))
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
            ("corpStationMgr", "DoStandingCheckForStationService") when pilot.Position is null && args is [(long)StationService.Fitting] =>
                PilotCallResult.None,
            ("corpFittingMgr", "GetCommunityFittings" or "GetFittings") or ("charFittingMgr", "GetFittings") => BlueEmpty.IndexedDictionary,
            ("itemLocking", "GetItemsByLocation") when args is [long location] && location == pilot.LocationId =>
                BlueEmpty.IndexedDictionary,
            ("invbroker", "GetInventory") when pilot.Position is null && args is [10004L, ..] => context.Reference(
                "@hangar",
                PilotBindingLifetime.Location
            ),
            ("invbroker", "GetInventoryFromId")
                when args is [long id, ..]
                    && fleet.Find(pilot.PilotId, id) is { } ship
                    && (id == pilot.ShipId || (pilot.Position is null && ship.LocationId == pilot.StationId)) => context.Reference(
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
            ("@hangar" or "@ship", "List") => List(pilot, service, args.FirstOrDefault() as long?),
            ("@pilot", "List") => BlueEmpty.List,
            ("@hangar" or "@ship", "Add" or "MultiAdd") => MoveFromWire(context, service, method, args, keywords),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => ShipDogmaBuilder.Item(pilot, service[1..]),
            ("dogmaIM", "GetAllInfo") => dogma.AllInfo(pilot),
            ("dogmaIM", "ItemGetInfo") when args is [long id] => ItemInfo(context, id),
            ("dogmaIM", "ItemGetInfo")
                when args is [object?[] { Length: 3 } key] && key is [long hull, long slot, long type] && hull == pilot.ShipId => ammunition
                .Read(pilot.PilotId, hull)
                .FirstOrDefault(m => m.FlagId == slot && m.TypeId == type)
                is { } magazine
                ? magazinesWire.Prime(magazine)
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
