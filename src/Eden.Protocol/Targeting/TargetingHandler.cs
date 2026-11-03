using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Targeting;

[Injectable(InjectionType.Scoped)]
internal sealed class TargetingHandler(TimeProvider clock, IPilotFitting fitting, SharedTargetLocks shared)
    : ISelectedPilotCallHandler,
        IDisposable
{
    private sealed record PendingLock(long Started, double Seconds);

    private readonly Dictionary<long, PendingLock> _pending = [];
    private readonly HashSet<long> _locked = [];
    private long[] _targeters = [];
    private long _shipId;

    public bool IsLocked(long id) => _locked.Contains(id);

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        if (_shipId != 0 && _shipId != context.Pilot.ShipId)
        {
            // Changing hulls clears both directions in the client's target UI.
            // Removing shared edges alone leaves portraits and targeted markers.
            foreach (long id in _pending.Keys.Concat(_locked).Distinct())
            {
                Notify(context, "lost", id);
            }
            foreach (long id in _targeters)
            {
                Notify(context, "otherlost", id);
            }
            shared.RemoveShip(_shipId);
            _pending.Clear();
            _locked.Clear();
            _targeters = [];
        }
        _shipId = context.Pilot.ShipId;
        if (!context.Flight.CanTarget)
        {
            foreach (long target in _pending.Keys.Concat(_locked).ToArray())
            {
                Remove(context, target);
            }
            shared.RemoveShip(_shipId);
        }
        else if (_pending.Count + _locked.Count > 0)
        {
            double range = fitting.ShipAttributes(context.Pilot.PilotId).GetValueOrDefault(DogmaAttributeId.MaxTargetRange);
            foreach (long id in _pending.Keys.Concat(_locked).ToArray())
            {
                var target = context.Flight.FindTarget(id);
                if (target is null || Distance(context, target) > range || (_locked.Contains(id) && !shared.Contains(_shipId, id)))
                {
                    Remove(context, id);
                }
                else if (_pending.TryGetValue(id, out var pending) && clock.GetElapsedTime(pending.Started).TotalSeconds >= pending.Seconds)
                {
                    _pending.Remove(id);
                    _locked.Add(id);
                    shared.Add(_shipId, id);
                    Notify(context, "add", id);
                }
            }
        }
        var targeters = shared.Targeters(_shipId);
        foreach (long id in _targeters.Except(targeters))
        {
            Notify(context, "otherlost", id);
        }
        foreach (long id in targeters.Except(_targeters))
        {
            Notify(context, "otheradd", id);
        }
        _targeters = targeters;
    }

    private static double Distance(PilotCallContext context, TargetContact target) =>
        Math.Max(0, context.Pilot.Position!.DistanceTo(target.Position) - context.Pilot.Flight.ShipRadius - target.Radius);

    private static void Notify(PilotCallContext context, string action, long target) =>
        context.Notifications.Add(("OnTarget", [action, target, null]));

    private void Remove(PilotCallContext context, long target)
    {
        bool pending = _pending.Remove(target);
        bool locked = _locked.Remove(target);
        shared.Remove(_shipId, target);
        if (pending || locked)
        {
            Notify(context, "lost", target);
        }
    }

    private object Add(PilotCallContext context, long id)
    {
        if (id == _shipId)
        {
            return new PilotCallRouter.UserError("DeniedTargetSelf", []);
        }
        if (context.Flight.FindTarget(id) is not { } target)
        {
            return new PilotCallRouter.UserError("TargetCantValidate", []);
        }
        if (_locked.Contains(id))
        {
            return new object?[] { false, _locked.Order().Cast<object?>().ToList() };
        }
        if (_pending.ContainsKey(id))
        {
            return new object?[] { true, _locked.Order().Cast<object?>().ToList() };
        }
        var attributes = fitting.ShipAttributes(context.Pilot.PilotId);
        int limit = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets));
        if (_pending.Count + _locked.Count >= limit)
        {
            return new PilotCallRouter.UserError("TargetingSystemsAlreadyFullyUtilized", new() { ["limit"] = limit });
        }
        if (Distance(context, target) > attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            return new PilotCallRouter.UserError("UnableToTarget", []);
        }
        double seconds = TargetingDefinitions.LockSeconds(
            attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution),
            fitting.Attributes(target.TypeId).GetValueOrDefault(DogmaAttributeId.SignatureRadius)
        );
        if (!(seconds > 0) || !double.IsFinite(seconds))
        {
            return new PilotCallRouter.UserError("TargetingSystemsNotInstalled", []);
        }
        _pending.Add(id, new(clock.GetTimestamp(), seconds));
        // targetMgr._LockTarget interprets true as pending; OnTarget("add") completes it.
        return new object?[] { true, _locked.Order().Cast<object?>().ToList() };
    }

    public object? Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "dogmaIM")
        {
            return PilotCallRouter.Unsupported;
        }
        context.Flight.Checkpoint();
        Advance(context);
        switch (method)
        {
            case "AddTarget" when args is [long target]:
                return Add(context, target);
            case "RemoveTarget" or "CancelAddTarget" when args is [long target]:
                Remove(context, target);
                return null;
            case "ClearTargets" when args.Length == 0:
                foreach (long id in _pending.Keys.Concat(_locked).ToArray())
                {
                    Remove(context, id);
                }
                return null;
            case "GetTargets" when args.Length == 0:
                return _locked.Order().Cast<object?>().ToList();
            case "GetTargeters" when args.Length == 0:
                return _targeters.Cast<object?>().ToList();
            default:
                return PilotCallRouter.Unsupported;
        }
    }

    public void Dispose() => shared.RemoveShip(_shipId);
}
