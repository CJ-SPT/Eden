using Eden.Gameplay.Fittings;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class SavedFittingsHandler(IPilotSavedFittings fittings) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "charFittingMgr"
            || method is not ("GetFittings" or "SaveFitting" or "UpdateFitting" or "DeleteFitting" or "UpdateNameAndDescription")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long owner = context.Pilot.PilotId;
            int ownerIndex = method == "UpdateNameAndDescription" ? 1 : 0;
            if (args.ElementAtOrDefault(ownerIndex) is not long requestedOwner || requestedOwner != owner)
            {
                throw new InvalidDataException("Read and save personal fittings for the selected pilot only.");
            }
            if (
                method == "UpdateFitting"
                && args is [_, var replacement]
                && keywords is { Count: 1 }
                && keywords.GetValueOrDefault("fittingID") is long updatedId
                && updatedId > 0
            )
            {
                return fittings.Save(owner, SavedFittingWire.Read(replacement, owner), updatedId);
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected saved fitting arguments.");
            }
            switch (method, args)
            {
                case ("GetFittings", [_]):
                    return new BlueMap<long, SavedFittingReply>(
                        fittings.Read(owner).ToDictionary(f => f.Id, f => new SavedFittingReply(f))
                    );
                case ("SaveFitting", [_, var plan]):
                    long createdId = fittings.Save(owner, SavedFittingWire.Read(plan, owner));
                    // PersistFitting does not cache newly saved fits itself. Native OnFittingAdded
                    // invalidates its primed dictionary so the next draw reloads the saved entry.
                    context.Notifications.Add(new FittingAddedNotification(owner, createdId));
                    return createdId;
                case ("UpdateFitting", [_, var plan, long id]) when id > 0:
                    return fittings.Save(owner, SavedFittingWire.Read(plan, owner), id);
                case ("DeleteFitting", [_, long id]) when id > 0:
                    fittings.Delete(owner, id);
                    return PilotCallResult.None;
                case ("UpdateNameAndDescription", [long id, _, var name, var description]) when id > 0:
                    fittings.Rename(owner, id, BlueMarshal.Text(name), BlueMarshal.Text(description));
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid saved fitting request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
