using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class InventoryHandler(TimeProvider clock) : IPilotCallHandler, ISelectedPilotCallHandler
{
    private static BlueInstance KeyVal(Dictionary<string, object?> values) => new("utillib.KeyVal", values);
    private static readonly (string Name, int Type)[] s_columns =
    [
        ("itemID", 20), ("typeID", 3), ("ownerID", 3), ("locationID", 20),
        ("flagID", 2), ("quantity", 3), ("groupID", 3), ("categoryID", 3),
        ("customInfo", 129), ("singleton", 2), ("stacksize", 3),
    ];

    internal static BlueConstructor ItemDescriptor() => new("blue.DBRowDescriptor",
        [s_columns.Select(column => (object?)new object?[] { column.Name, column.Type }).ToArray()]);

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

    public object? Call(PilotCallContext context, string service, string method, object? arguments)
        => (service, method) == ("invbroker", "GetItemDescriptor") ? ItemDescriptor() : PilotCallRouter.Unsupported;

    public object? Call(PilotCallContext context, string service, string method, object?[] args, Dictionary<string, object?>? keywords = null)
    {
        var pilot = context.Pilot;
        return (service, method) switch
        {
            ("invbroker", "GetInventory") when pilot.Position is null && args is [10004L, ..] => context.Reference("@hangar"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.ShipId) => context.Reference("@ship"),
            ("invbroker", "GetInventoryFromId") when args.Length > 0 && Equals(args[0], pilot.PilotId) => context.Reference("@pilot"),
            ("@hangar", "List") => pilot.Position is null && (args.Length == 0 || args[0] is null or 4L) ? new List<object?> { Item(pilot, "ship") } : new List<object?>(),
            ("@ship" or "@pilot", "List") => new List<object?>(),
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
