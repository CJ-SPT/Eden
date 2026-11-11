using Eden.Gameplay.Fittings;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Fittings;

[Injectable(InjectionType.Scoped)]
internal sealed class SavedFittingMonitor(IPilotSavedFittings fittings)
{
    private sealed record Snapshot(long Revision, IReadOnlyDictionary<long, SavedFitting> Fittings);

    private readonly Dictionary<long, Snapshot> _owners = [];
    private long? _pilotId;
    public bool IsMonitoring => _owners.Count > 0;

    public void Reset()
    {
        _owners.Clear();
        _pilotId = null;
    }

    public IReadOnlyList<SavedFitting> Read(PilotCallContext context, long ownerId)
    {
        Select(context);
        long revision = fittings.Revision(ownerId);
        var current = fittings.Read(context.Pilot.PilotId, ownerId);
        _owners[ownerId] = new(revision, current.ToDictionary(f => f.Id));
        return current;
    }

    public void Refresh(PilotCallContext context, long ownerId, IReadOnlyList<long> acknowledgedIds)
    {
        if (_owners.TryGetValue(ownerId, out var before))
        {
            Reconcile(context, ownerId, before, acknowledgedIds);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        Select(context);
        foreach (long ownerId in _owners.Keys.ToArray())
        {
            if (ownerId != context.Pilot.PilotId && ownerId != context.Pilot.CorpId)
            {
                _owners.Remove(ownerId);
                continue;
            }
            var before = _owners[ownerId];
            if (fittings.Revision(ownerId) == before.Revision)
            {
                continue;
            }
            Reconcile(context, ownerId, before, []);
        }
    }

    private void Reconcile(PilotCallContext context, long ownerId, Snapshot before, IReadOnlyList<long> acknowledgedIds)
    {
        IReadOnlyList<SavedFitting> current;
        try
        {
            current = Read(context, ownerId);
        }
        catch (InvalidDataException)
        {
            // Membership can change before the session identity is refreshed.
            _owners.Remove(ownerId);
            return;
        }
        var acknowledged = acknowledgedIds.ToHashSet();
        var deleted = before.Fittings.Keys.Except(current.Select(f => f.Id)).Where(id => !acknowledged.Contains(id)).ToArray();
        if (deleted.Length > 0)
        {
            context.Notifications.Add(new FittingsDeletedNotification(ownerId, deleted));
        }
        foreach (var added in current.Where(f => !before.Fittings.ContainsKey(f.Id) && !acknowledged.Contains(f.Id)))
        {
            context.Notifications.Add(new FittingAddedNotification(ownerId, added.Id));
        }
        var updated = current
            .Where(f => !acknowledged.Contains(f.Id) && before.Fittings.TryGetValue(f.Id, out var previous) && !Same(previous, f))
            .Select(f => f.Id)
            .ToArray();
        if (updated.Length > 0)
        {
            context.Notifications.Add(new FittingsUpdatedNotification(ownerId, updated));
        }
    }

    private void Select(PilotCallContext context)
    {
        if (_pilotId != context.Pilot.PilotId)
        {
            Reset();
            _pilotId = context.Pilot.PilotId;
        }
    }

    private static bool Same(SavedFitting before, SavedFitting after) =>
        before.SavedAt == after.SavedAt
        && before.Plan.Name == after.Plan.Name
        && before.Plan.Description == after.Plan.Description
        && before.Plan.ShipTypeId == after.Plan.ShipTypeId
        && before.Plan.Items.SequenceEqual(after.Plan.Items);
}
