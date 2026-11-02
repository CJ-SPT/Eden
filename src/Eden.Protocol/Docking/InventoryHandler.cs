using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class InventoryHandler(TimeProvider clock, IPilotInventory inventory) : IPilotCallHandler, ISelectedPilotCallHandler
{
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);
    private static readonly (string Name, int Type)[] s_columns =
    [
        ("itemID", 20), ("typeID", 3), ("ownerID", 3), ("locationID", 20),
        ("flagID", 2), ("quantity", 3), ("groupID", 3), ("categoryID", 3),
        ("customInfo", 129), ("stacksize", 3), ("singleton", 2),
    ];

    internal static BlueConstructor ItemDescriptor() => new("blue.DBRowDescriptor",
        [s_columns.Select(column => (object?)new object?[] { column.Name, column.Type }).ToArray()]);

    internal static BlueConstructor Item(InventoryItem item) => new("blue.DBRow", [ItemDescriptor(), new object?[]
    {
        item.Id, item.TypeId, item.OwnerId, item.LocationId, item.FlagId, item.Quantity,
        item.GroupId, item.CategoryId, "", item.Quantity, 0,
    }]);

    private List<object?> List(DockedPilot pilot, string service, object?[] args)
    {
        List<object?> result = [];
        if (service == "@hangar" && pilot.Position is not null)
        {
            return result;
        }
        long location = service == "@hangar" ? pilot.StationId : pilot.ShipId;
        var flag = args.FirstOrDefault() as long?;
        if (service == "@hangar" && (flag is null or 4))
        {
            result.Add(Item(pilot, "ship"));
        }
        result.AddRange(inventory.List(pilot.PilotId, location).Where(i => flag is null || i.FlagId == flag).Select(Item));
        return result;
    }

    private object? Move(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (args.Length != 2 || args[1] is not long source)
        {
            return PilotCallRouter.Unsupported;
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
            return PilotCallRouter.Unsupported;
        }
        long destination = service == "@hangar" ? context.Pilot.StationId : context.Pilot.ShipId;
        object? flagValue = keywords?.GetValueOrDefault("flag");
        object? quantityValue = keywords?.GetValueOrDefault("qty");
        if ((flagValue is not null && flagValue is not long) || (quantityValue is not null && quantityValue is not long) ||
            (flagValue is long flagNumber && (flagNumber < 0 || flagNumber > int.MaxValue)) ||
            (quantityValue is long quantityNumber && (quantityNumber <= 0 || quantityNumber > int.MaxValue)))
        {
            return new PilotCallRouter.Error("Invalid cargo flag or quantity.");
        }
        int flag = flagValue is long f ? (int)f : service == "@hangar" ? 4 : 5;
        int? quantity = quantityValue is long q ? (int)q : null;
        try
        {
            var changes = inventory.Move(context.Pilot.PilotId, ids, source, destination, flag, quantity);
            foreach (var change in changes)
            {
                // OnItemChange keys index the inventory DBRow, and values describe the old row.
                Dictionary<long, object?> old = [];
                if (change.Previous is not { } previous)
                {
                    old[5] = 0;
                    old[9] = 0;
                }
                else
                {
                    if (previous.LocationId != change.Item.LocationId) { old[3] = previous.LocationId; }
                    if (previous.FlagId != change.Item.FlagId) { old[4] = previous.FlagId; }
                    if (previous.Quantity != change.Item.Quantity)
                    {
                        old[5] = previous.Quantity;
                        old[9] = previous.Quantity;
                    }
                }
                context.Notifications.Add(("OnItemChange", [Item(change.Item), old, null]));
            }
            return null;
        }
        catch (CargoCapacityException error)
        {
            return new PilotCallRouter.UserError("NotEnoughCargoSpace", new()
            {
                ["available"] = error.Available,
                ["volume"] = error.Required,
            });
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
    }

    private BlueConstructor Item(DockedPilot pilot, string kind)
    {
        object?[] line = kind switch
        {
            "ship" => [pilot.ShipId, pilot.ShipTypeId, pilot.PilotId, pilot.LocationId, pilot.Position is null ? 4 : 0, -1, pilot.ShipGroupId, pilot.ShipCategoryId, "", 1, 1],
            "pilot" => [pilot.PilotId, 1373, pilot.PilotId, pilot.ShipId, 57, -1, 1, 1, "", 1, 1],
            _ => [pilot.StationId, pilot.StationTypeId, pilot.StationOwnerId, pilot.SystemId, 0, -1, pilot.Station.GroupId, pilot.Station.CategoryId, "", 1, 1],
        };
        return new("blue.DBRow", [ItemDescriptor(), line]);
    }

    private Dictionary<long, object?> PrimeItem(DockedPilot pilot, long id, string kind)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var attributes = kind == "pilot" ? PilotProfile.TrainingAttributes() : new Dictionary<long, object?>();
        if (kind != "pilot")
        {
            // Undamaged, fully charged Ibis. The HUD reads these current values from godma.
            attributes[9] = pilot.Hull.Structure;
            attributes[18] = pilot.Hull.Capacitor;
            attributes[37] = (double)pilot.Flight.MaxSpeed;
            attributes[38] = pilot.CargoCapacity;
            attributes[55] = pilot.Hull.RechargeTime;
            attributes[263] = pilot.Hull.Shield;
            attributes[264] = pilot.Hull.Shield;
            attributes[265] = pilot.Hull.Armor;
            attributes[479] = pilot.Hull.ShieldRechargeTime;
            attributes[482] = pilot.Hull.Capacitor;
        }
        return new()
        {
            [id] = KeyVal(new()
            {
                ["itemID"] = id,
                ["invItem"] = Item(pilot, kind),
                ["attributes"] = attributes,
                ["activeEffects"] = new Dictionary<long, object?>(),
                ["time"] = now,
                ["wallclockTime"] = now
            })
        };
    }

    public object? Call(PilotCallContext context, string service, string method, object? arguments, Dictionary<string, object?>? keywords)
        => (service, method) == ("invbroker", "GetItemDescriptor") ? ItemDescriptor() : PilotCallRouter.Unsupported;

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        var pilot = context.Pilot;
        return (service, method) switch
        {
            ("itemLocking", "GetItemsByLocation") when args is [long location] && location == pilot.LocationId => new Dictionary<long, object?>(),
            ("invbroker", "GetInventory") when pilot.Position is null && args is [10004L, ..] => context.Reference("@hangar"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.ShipId) => context.Reference("@ship"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.PilotId) => context.Reference("@pilot"),
            ("@hangar" or "@ship", "List") => List(pilot, service, args),
            ("@pilot", "List") => new List<object?>(),
            ("@hangar" or "@ship", "Add" or "MultiAdd") => Move(context, service, method, args, keywords),
            ("@hangar" or "@ship" or "@pilot", "GetSelfInvItem") => Item(pilot, service[1..]),
            ("dogmaIM", "GetAllInfo") => KeyVal(new()
            {
                // Instance rows follow dgmAttributesByIdx: online, damage, charge, SP, armor damage, shield, incapacitated.
                ["shipState"] = new object?[] { new Dictionary<long, object?>
                {
                    [pilot.PilotId] = new object?[] { pilot.PilotId, 0, 0, 0, 0, 0, 0, 0 },
                    [pilot.ShipId] = new object?[] { pilot.ShipId, 0, 0, pilot.Hull.Capacitor, 0, 0, pilot.Hull.Shield, 0 },
                }, new Dictionary<long, object?>(), new Dictionary<long, object?>(), new List<object?>() },
                ["structureInfo"] = null,
                ["shipInfo"] = PrimeItem(pilot, pilot.ShipId, "ship"),
                ["charInfo"] = new object?[] { PrimeItem(pilot, pilot.PilotId, "pilot"), new object?[] { 0, Array.Empty<object?>(), Array.Empty<object?>(), Array.Empty<object?>() } },
                ["shipModifiedCharAttribs"] = null,
                ["systemWideEffectsOnShip"] = null,
            }),
            _ => PilotCallRouter.Unsupported,
        };
    }
}
