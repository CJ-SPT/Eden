using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Corporations;

[Injectable]
internal sealed class CorporationBlueprintLockHandler(ICorporationBlueprintLocks locks) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "itemLocking" || method is not ("GetItemsByLocation" or "GetLockedItemLocations"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected blueprint lock arguments.");
            }
            return (method, args) switch
            {
                ("GetItemsByLocation", [long location]) when location > 0 => new CorporationBlueprintLocksReply(
                    locks.Read(context.Pilot.PilotId, location)
                ),
                ("GetLockedItemLocations", []) => new CorporationBlueprintLockLocationsReply(locks.Read(context.Pilot.PilotId)),
                _ => new PilotCallResult.Failure("Choose a valid blueprint lock location."),
            };
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}

internal sealed record CorporationBlueprintLocksReply(IReadOnlyList<CorporationBlueprintLock> Locks) : BlueModel
{
    public override object ToBlue() =>
        Locks.ToDictionary(
            row => row.ItemId,
            row =>
                (object?)
                    new BlueInstance(
                        "utillib.KeyVal",
                        new Dictionary<string, object?>
                        {
                            ["itemID"] = row.ItemId,
                            ["typeID"] = row.TypeId,
                            ["ownerID"] = row.CorporationId,
                            // The native service indexes office contents by station, not by the inventory's office ID.
                            ["locationID"] = row.StationId,
                        }
                    )
        );
}

internal sealed record CorporationBlueprintLockLocationsReply(IReadOnlyList<CorporationBlueprintLock> Locks) : BlueModel
{
    public override object ToBlue() =>
        new BlueConstructor("__builtin__.set", [Locks.Select(row => (long)row.StationId).Distinct().Select(id => (object?)id).ToList()]);
}

internal sealed record CorporationBlueprintLockNotification(CorporationBlueprintLockChange Change) : PilotNotification("OnLockedItemChange")
{
    public override object?[] Arguments => [Change.ItemId, Change.CorporationId, Change.StationId, Change.Locked];
}
