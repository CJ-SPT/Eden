using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Scoped)]
internal sealed class AssetSafetyHandler(IPilotAssetSafety safety, TimeProvider clock) : ISelectedPilotCallHandler
{
    internal const string InventoryPrefix = "@assetSafety:";
    private (long Pilot, int Corporation, bool Director) _owner;
    private long _personalRevision = -1;
    private long _corporationRevision = -1;
    private long _deadline = long.MaxValue;
    private Dictionary<long, AssetSafetyWrap> _pending = [];
    internal bool IsMonitoring => _pending.Count != 0;

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected || !safety.Available)
        {
            return;
        }
        var pilot = context.Pilot;
        bool director = (pilot.Identity.CorporationRoles & (long)CorporationRole.Director) != 0;
        var owner = (pilot.PilotId, pilot.CorpId, director);
        long personal = safety.Revision(pilot.PilotId);
        long corporation = director ? safety.Revision(pilot.CorpId) : 0;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (_owner == owner && personal == _personalRevision && corporation == _corporationRevision && now < _deadline)
        {
            return;
        }
        var pending = safety.List(pilot.PilotId, false).Concat(director ? safety.List(pilot.PilotId, true) : []).ToDictionary(w => w.Id);
        if (_owner == owner)
        {
            foreach (var wrap in pending.Values.Where(w => !_pending.ContainsKey(w.Id)))
            {
                if (
                    !context
                        .Notifications.OfType<AssetSafetyCreated>()
                        .Any(n => n.OwnerId == wrap.OwnerId && n.StructureId == wrap.StructureId && n.SystemId == wrap.SystemId)
                )
                {
                    context.Notifications.Add(new AssetSafetyCreated(wrap.OwnerId, wrap.SystemId, wrap.StructureId));
                }
            }
            foreach (long delivered in _pending.Values.Where(w => !pending.ContainsKey(w.Id)).Select(w => w.OwnerId).Distinct())
            {
                if (!context.Notifications.OfType<AssetSafetyDelivered>().Any(n => n.OwnerId == delivered))
                {
                    context.Notifications.Add(new AssetSafetyDelivered(delivered));
                }
            }
        }
        _owner = owner;
        _pending = pending;
        _personalRevision = safety.Revision(pilot.PilotId);
        _corporationRevision = director ? safety.Revision(pilot.CorpId) : 0;
        _deadline = pending.Values.Select(w => w.AutomaticAt).DefaultIfEmpty(long.MaxValue).Min();
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        long? inventory =
            service.StartsWith(InventoryPrefix, StringComparison.Ordinal)
            && long.TryParse(service.AsSpan(InventoryPrefix.Length), out long bound)
                ? bound
                : null;
        bool manager =
            service == "invbroker"
            && method is "GetInventoryFromId" or "GetContainerContents"
            && args is [long id, ..]
            && safety.Handles(id);
        if (service != "structureAssetSafety" && inventory is null && !manager)
        {
            return PilotCallResult.Unsupported;
        }
        if (
            service == "structureAssetSafety"
            && method
                is not (
                    "GetItemsInSafetyForCharacter"
                    or "GetItemsInSafetyForCorp"
                    or "GetWrapNames"
                    or "GetStructuresICanDeliverTo"
                    or "MovePersonalAssetsToSafety"
                    or "MoveCorpAssetsToSafety"
                    or "MoveSafetyWrapToStructure"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long pilot = context.Pilot.PilotId;
            if (inventory is long container)
            {
                if (keywords is { Count: > 0 } || args.Length > 1 || args.Length == 1 && args[0] is not (null or long))
                {
                    throw new InvalidDataException("Invalid safety inventory arguments.");
                }
                var contents = safety.Inventory(pilot, container);
                return method switch
                {
                    "List" => new BlueList<InventoryRow>(
                        contents
                            .Items.Where(item => args.Length == 0 || args[0] is null || Equals(args[0], (long)item.FlagId))
                            .Select(InventoryRow.From)
                            .ToArray()
                    ),
                    "GetSelfInvItem" when args.Length == 0 => InventoryRow.From(contents.Wrap),
                    "GetCapacity" => new CargoContainerCapacity(0, contents.Items.Sum(item => item.Volume * item.Quantity)),
                    _ => PilotCallResult.Unsupported,
                };
            }
            if (manager)
            {
                if (args is not [long itemId, ..] || args.Length is < 1 or > 2 || keywords is { Count: > 0 })
                {
                    throw new InvalidDataException("Choose one safety inventory.");
                }
                var contents = safety.Inventory(pilot, itemId);
                return method == "GetInventoryFromId"
                    ? context.Reference($"{InventoryPrefix}{itemId}", PilotBindingLifetime.Location)
                    : new BlueList<InventoryRow>(contents.Items.Select(InventoryRow.From).ToArray());
            }
            if (method == "MoveSafetyWrapToStructure")
            {
                if (
                    args is not [long wrap, long system]
                    || system is <= 0 or > int.MaxValue
                    || keywords is not { Count: 1 }
                    || keywords.GetValueOrDefault("destinationID") is not long destination
                )
                {
                    throw new InvalidDataException("Choose a safety wrap, original system and delivery destination.");
                }
                var delivered = safety.Deliver(pilot, wrap, (int)system, destination);
                context.Notifications.Add(new AssetSafetyDelivered(delivered.OwnerId));
                return PilotCallResult.None;
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected asset safety arguments.");
            }
            switch (method, args)
            {
                case ("GetItemsInSafetyForCharacter", []):
                    return new PersonalAssetSafetyItems(safety.Available ? safety.List(pilot, false) : []);
                case ("GetItemsInSafetyForCorp", []):
                    return new PersonalAssetSafetyItems(safety.List(pilot, true));
                case ("GetWrapNames", [List<object?> ids]) when ids.All(id => id is long):
                    return new AssetSafetyNames(safety.Names(pilot, ids.Cast<long>().ToArray()));
                case ("GetStructuresICanDeliverTo", [long system]) when system is > 0 and <= int.MaxValue:
                    return new AssetSafetyDestinations(safety.Destinations(pilot, (int)system));
                case ("MovePersonalAssetsToSafety" or "MoveCorpAssetsToSafety", [long system, long structure])
                    when system is > 0 and <= int.MaxValue:
                    var moved = safety.Move(pilot, (int)system, structure, method == "MoveCorpAssetsToSafety");
                    if (moved is not null)
                    {
                        context.Notifications.Add(new AssetSafetyCreated(moved.OwnerId, moved.SystemId, moved.StructureId));
                    }
                    context.Flight.RefreshWrecks();
                    return PilotCallResult.None;
                default:
                    return new PilotCallResult.Failure("Invalid asset safety request.");
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException or InvalidOperationException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record PersonalAssetSafetyItems(IReadOnlyList<AssetSafetyWrap> Wraps) : BlueModel
{
    public override object ToBlue() =>
        Wraps
            .Select(w =>
                (object?)
                    new Dictionary<string, object?>
                    {
                        ["solarSystemID"] = w.SystemId,
                        ["assetWrapID"] = w.Id,
                        ["wrapName"] = new BlueUnicode(w.Name),
                        ["ejectTime"] = w.CreatedAt,
                        ["daysUntilCanDeliverConst"] = AssetSafetyRules.ManualDays,
                        ["daysUntilAutoMoveConst"] = AssetSafetyRules.AutomaticDays,
                        ["nearestNPCStationInfo"] = new AssetSafetyDestinationReply(w.NearestStation).ToBlue(),
                    }
            )
            .ToList();
}

internal sealed record AssetSafetyDestinationReply(AssetSafetyDestination Destination) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "util.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Destination.Id,
                ["typeID"] = Destination.TypeId,
                ["solarSystemID"] = Destination.SystemId,
                ["itemName"] = new BlueUnicode(Destination.Name),
            }
        );
}

internal sealed record AssetSafetyDestinations(AssetSafetyTargets Targets) : BlueModel
{
    public override object ToBlue() =>
        new object?[]
        {
            Targets.Structures.Select(d => new AssetSafetyDestinationReply(d).ToBlue()).ToList(),
            Targets.NearestStation is { } station ? new AssetSafetyDestinationReply(station).ToBlue() : null,
        };
}

internal sealed record AssetSafetyNames(IReadOnlyDictionary<long, string?> Names) : BlueModel
{
    public override object ToBlue() => Names.ToDictionary(p => p.Key, p => p.Value is { } name ? (object)new BlueUnicode(name) : null);
}

internal sealed record AssetSafetyCreated(long OwnerId, int SystemId, long StructureId) : PilotNotification("OnAssetSafetyCreated")
{
    public override object?[] Arguments => [OwnerId, SystemId, StructureId];
}

internal sealed record AssetSafetyDelivered(long OwnerId) : PilotNotification("OnAssetSafetyDelivered")
{
    public override object?[] Arguments => [OwnerId];
}
