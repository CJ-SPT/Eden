using Eden.Gameplay.Fittings;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Fittings;

[Injectable]
internal sealed class SavedFittingsHandler(IPilotSavedFittings fittings, SavedFittingMonitor monitor) : ISelectedPilotCallHandler
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
            service is not ("charFittingMgr" or "corpFittingMgr")
            || method
                is not (
                    "GetFittings"
                    or "SaveFitting"
                    or "UpdateFitting"
                    or "DeleteFitting"
                    or "UpdateNameAndDescription"
                    or "SaveManyFittings"
                    or "DeleteManyFittings"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            long pilotId = context.Pilot.PilotId;
            long owner = service == "charFittingMgr" ? pilotId : context.Pilot.CorpId;
            int ownerIndex = method == "UpdateNameAndDescription" ? 1 : 0;
            if (args.ElementAtOrDefault(ownerIndex) is not long requestedOwner || requestedOwner != owner)
            {
                throw new InvalidDataException("Choose the selected pilot or their current corporation as fitting owner.");
            }
            monitor.Advance(context);
            if (
                method == "UpdateFitting"
                && args is [_, var replacement]
                && keywords is { Count: 1 }
                && keywords.GetValueOrDefault("fittingID") is long updatedId
                && updatedId > 0
            )
            {
                long savedId = fittings.Save(pilotId, SavedFittingWire.Read(replacement, owner), updatedId, owner);
                context.Notifications.Add(new FittingsUpdatedNotification(owner, [savedId]));
                monitor.Refresh(context, owner, [savedId]);
                return savedId;
            }
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected saved fitting arguments.");
            }
            switch (method, args)
            {
                case ("GetFittings", [_]):
                    return new BlueMap<long, SavedFittingReply>(
                        monitor.Read(context, owner).ToDictionary(f => f.Id, f => new SavedFittingReply(f))
                    );
                case ("SaveFitting", [_, var plan]):
                    long createdId = fittings.Save(pilotId, SavedFittingWire.Read(plan, owner), ownerId: owner);
                    // PersistFitting does not cache newly saved fits itself. Native OnFittingAdded
                    // invalidates its primed dictionary so the next draw reloads the saved entry.
                    context.Notifications.Add(new FittingAddedNotification(owner, createdId));
                    monitor.Refresh(context, owner, [createdId]);
                    return createdId;
                case ("UpdateFitting", [_, var plan, long id]) when id > 0:
                    long savedId = fittings.Save(pilotId, SavedFittingWire.Read(plan, owner), id, owner);
                    context.Notifications.Add(new FittingsUpdatedNotification(owner, [savedId]));
                    monitor.Refresh(context, owner, [savedId]);
                    return savedId;
                case ("DeleteFitting", [_, long id]) when id > 0:
                    fittings.Delete(pilotId, id, owner);
                    context.Notifications.Add(new FittingDeletedNotification(owner, id));
                    monitor.Refresh(context, owner, [id]);
                    return PilotCallResult.None;
                case ("UpdateNameAndDescription", [long id, _, var name, var description]) when id > 0:
                    fittings.Rename(pilotId, id, BlueMarshal.Text(name), BlueMarshal.Text(description), owner);
                    context.Notifications.Add(new FittingsUpdatedNotification(owner, [id]));
                    monitor.Refresh(context, owner, [id]);
                    return PilotCallResult.None;
                case ("SaveManyFittings", [_, var plans]):
                    var mappings = fittings.SaveMany(pilotId, owner, SavedFittingWire.ReadMany(plans, owner));
                    foreach (var mapping in mappings)
                    {
                        context.Notifications.Add(new FittingAddedNotification(owner, mapping.FittingId));
                    }
                    monitor.Refresh(context, owner, mappings.Select(m => m.FittingId).ToArray());
                    return new BlueList<FittingImportReply>(mappings.Select(m => new FittingImportReply(m)).ToArray());
                case ("DeleteManyFittings", [_, var ids]):
                    var deleted = fittings.DeleteMany(pilotId, owner, SavedFittingWire.ReadIds(ids));
                    if (deleted.Count > 0)
                    {
                        context.Notifications.Add(new FittingsDeletedNotification(owner, deleted));
                    }
                    monitor.Refresh(context, owner, deleted);
                    return new BlueList<long>(deleted);
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
