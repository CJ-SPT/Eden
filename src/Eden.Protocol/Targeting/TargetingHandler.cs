using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Targeting;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Targeting;

[Injectable(InjectionType.Scoped)]
internal sealed class TargetingHandler(
    TimeProvider clock,
    IPilotFitting fitting,
    SharedTargetLocks shared,
    INpcElectronicWarfare? npcEwar = null
) : ISelectedPilotCallHandler, IDisposable
{
    private sealed record PendingLock(long Started, double Seconds)
    {
        internal TaskCompletionSource<PilotCallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
                Notify(context, TargetChange.Lost, id);
            }
            foreach (long id in _targeters)
            {
                Notify(context, TargetChange.OtherLost, id);
            }
            shared.RemoveShip(_shipId);
            CancelPendingReplies();
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
            var incoming = npcEwar?.Incoming(_shipId) ?? [];
            foreach (long id in _pending.Keys.Concat(_locked).ToArray())
            {
                var target = context.Flight.FindTarget(id);
                if (
                    target is null
                    || Distance(context, target) > range
                    || !NpcEwarRules.CanTarget(incoming, id)
                    || (_locked.Contains(id) && !shared.Contains(_shipId, id))
                )
                {
                    Remove(context, id);
                }
                else if (_pending.TryGetValue(id, out var pending) && clock.GetElapsedTime(pending.Started).TotalSeconds >= pending.Seconds)
                {
                    _pending.Remove(id);
                    _locked.Add(id);
                    shared.Add(_shipId, id);
                    Notify(context, TargetChange.Added, id);
                    pending.Completion.TrySetResult(new PilotCallResult.Success(new TargetLockReply(true, _locked.Order().ToArray())));
                }
            }
        }
        var targeters = shared.Targeters(_shipId);
        foreach (long id in _targeters.Except(targeters))
        {
            Notify(context, TargetChange.OtherLost, id);
        }
        foreach (long id in targeters.Except(_targeters))
        {
            Notify(context, TargetChange.OtherAdded, id);
        }
        _targeters = targeters;
    }

    private static double Distance(PilotCallContext context, TargetContact target) =>
        Math.Max(0, context.Pilot.Position!.DistanceTo(target.Position) - context.Pilot.Flight.ShipRadius - target.Radius);

    private static void Notify(PilotCallContext context, TargetChange action, long target) =>
        context.Notifications.Add(new TargetChanged(action, target));

    private void Remove(PilotCallContext context, long target)
    {
        bool pending = _pending.Remove(target, out var scan);
        scan?.Completion.TrySetResult(CancelledLock());
        bool locked = _locked.Remove(target);
        shared.Remove(_shipId, target);
        if (pending || locked)
        {
            Notify(context, TargetChange.Lost, target);
        }
    }

    private PilotCallResult Add(PilotCallContext context, long id)
    {
        if (id == _shipId)
        {
            return new PilotCallResult.NamedFailure("DeniedTargetSelf", BlueEmpty.Dictionary);
        }
        if (context.Flight.FindTarget(id) is not { } target)
        {
            return new PilotCallResult.NamedFailure("TargetCantValidate", BlueEmpty.Dictionary);
        }
        if (_locked.Contains(id))
        {
            return new TargetLockReply(false, _locked.Order().ToArray());
        }
        if (_pending.TryGetValue(id, out var scanning))
        {
            return new TargetLockReply(true, _locked.Order().ToArray()) { Completion = scanning.Completion.Task };
        }
        if (!NpcEwarRules.CanTarget(npcEwar?.Incoming(context.Pilot.ShipId) ?? [], id))
        {
            return new PilotCallResult.Failure("Electronic countermeasures prevent locking this target.");
        }
        var attributes = fitting.ShipAttributes(context.Pilot.PilotId);
        int limit = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets));
        if (_pending.Count + _locked.Count >= limit)
        {
            return new PilotCallResult.NamedFailure("TargetingSystemsAlreadyFullyUtilized", new TargetLimitErrorValues(limit));
        }
        if (Distance(context, target) > attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
        {
            return new PilotCallResult.NamedFailure("UnableToTarget", BlueEmpty.Dictionary);
        }
        double seconds = TargetingDefinitions.LockSeconds(
            attributes.GetValueOrDefault(DogmaAttributeId.ScanResolution),
            fitting.SignatureRadius(target.Id, target.TypeId)
        );
        if (!(seconds > 0) || !double.IsFinite(seconds))
        {
            return new PilotCallResult.NamedFailure("TargetingSystemsNotInstalled", BlueEmpty.Dictionary);
        }
        PendingLock pending = new(clock.GetTimestamp(), seconds);
        _pending.Add(id, pending);
        // OnTarget("add") supplies success. If this scan is interrupted, the
        // outstanding RPC must fail so native FailLockTarget clears its animation.
        return new TargetLockReply(true, _locked.Order().ToArray()) { Completion = pending.Completion.Task };
    }

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        bool handled = (method, args) switch
        {
            ("AddTarget" or "RemoveTarget" or "CancelAddTarget", [long]) => true,
            ("ClearTargets" or "GetTargets" or "GetTargeters", []) => true,
            _ => false,
        };
        if (service != "dogmaIM" || !handled)
        {
            return PilotCallResult.Unsupported;
        }
        context.Flight.Checkpoint();
        Advance(context);
        switch (method)
        {
            case "AddTarget" when args is [long target]:
                return Add(context, target);
            case "RemoveTarget" or "CancelAddTarget" when args is [long target]:
                Remove(context, target);
                return PilotCallResult.None;
            case "ClearTargets" when args.Length == 0:
                foreach (long id in _pending.Keys.Concat(_locked).ToArray())
                {
                    Remove(context, id);
                }
                return PilotCallResult.None;
            case "GetTargets" when args.Length == 0:
                return new BlueList<long>(_locked.Order().ToArray());
            case "GetTargeters" when args.Length == 0:
                return new BlueList<long>(_targeters);
            default:
                return PilotCallResult.Unsupported;
        }
    }

    public void Reset()
    {
        shared.RemoveShip(_shipId);
        CancelPendingReplies();
        _pending.Clear();
        _locked.Clear();
        _targeters = [];
        _shipId = 0;
    }

    public void Dispose() => Reset();

    private static PilotCallResult.NamedFailure CancelledLock() => new("TargetingAttemptCancelled", BlueEmpty.Dictionary);

    private void CancelPendingReplies()
    {
        foreach (var pending in _pending.Values)
        {
            pending.Completion.TrySetResult(CancelledLock());
        }
    }
}
