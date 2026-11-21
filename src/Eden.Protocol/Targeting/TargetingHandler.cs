using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Structures;

namespace Eden.Protocol.Targeting;

[Injectable(InjectionType.Scoped)]
internal sealed class TargetingHandler(
    TimeProvider clock,
    IPilotFitting fitting,
    SharedTargetLocks shared,
    INpcElectronicWarfare? npcEwar = null,
    IPilotElectronicWarfare? electronicWarfare = null
) : ISelectedPilotCallHandler, IDisposable
{
    private sealed record PendingLock(long Started, double Seconds, bool Silent = false)
    {
        internal TaskCompletionSource<PilotCallResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<long, PendingLock> _pending = [];
    private readonly HashSet<long> _locked = [];
    private readonly HashSet<long> _miningLocks = [];
    private readonly HashSet<long> _miningExcludedTargets = [];
    private long[] _targeters = [];
    private long _shipId;
    private long _breakRevision;
    private StructureControlLease? _structureControl;

    public bool IsLocked(long id) =>
        _locked.Contains(id) && shared.Contains(_shipId, id) && shared.BreakRevision(_shipId) == _breakRevision;

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        long source = context.ControlledStructureId ?? context.Pilot.ShipId;
        bool changedShip = _shipId != source || _structureControl != context.StructureControl;
        if (_shipId != 0 && changedShip)
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
            if (_structureControl is null)
            {
                shared.RemoveShip(_shipId);
            }
            CancelPendingReplies();
            _pending.Clear();
            _locked.Clear();
            _miningLocks.Clear();
            _miningExcludedTargets.Clear();
            _targeters = [];
        }
        _shipId = source;
        _structureControl = context.StructureControl;
        var structure = context.Flight.StructureTargetingSource;
        long revision = shared.BreakRevision(_shipId);
        if (!changedShip && revision != _breakRevision)
        {
            foreach (long target in _pending.Keys.Concat(_locked).ToArray())
            {
                Remove(context, target);
            }
        }
        _breakRevision = revision;
        if (structure is null && !context.Flight.CanAcquireTargets)
        {
            foreach (long target in _pending.Keys.Concat(_locked).ToArray())
            {
                Remove(context, target);
            }
            if (_structureControl is null)
            {
                shared.RemoveShip(_shipId);
            }
        }
        else if (_pending.Count + _locked.Count > 0)
        {
            double range = Attributes(context, structure).GetValueOrDefault(DogmaAttributeId.MaxTargetRange);
            var incoming = Jamming(_shipId);
            foreach (long id in _pending.Keys.Concat(_locked).ToArray())
            {
                var target = FindTarget(context, id);
                if (
                    target is null
                    || Distance(context, structure, target) > range
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
                    shared.Add(_shipId, id, pending.Silent);
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

    private IReadOnlyDictionary<DogmaAttributeId, double> Attributes(PilotCallContext context, StaticSpaceObject? structure) =>
        structure is null ? fitting.ShipAttributes(context.Pilot.PilotId) : fitting.Attributes(structure.TypeId);

    private static TargetContact? FindTarget(PilotCallContext context, long id) =>
        context.ControlledStructureId is null ? context.Flight.FindTarget(id) : context.Flight.FindStructureTarget(id);

    private static double Distance(PilotCallContext context, StaticSpaceObject? structure, TargetContact target)
    {
        var position = structure?.Position ?? context.Pilot.Position!;
        double radius = structure?.Radius ?? context.Pilot.Flight.ShipRadius;
        return Math.Max(0, position.DistanceTo(target.Position) - radius - target.Radius);
    }

    private static void Notify(PilotCallContext context, TargetChange action, long target) =>
        context.Notifications.Add(new TargetChanged(action, target));

    private void Remove(PilotCallContext context, long target)
    {
        _miningLocks.Remove(target);
        bool pending = _pending.Remove(target, out var scan);
        scan?.Completion.TrySetResult(CancelledLock());
        bool locked = _locked.Remove(target);
        shared.Remove(_shipId, target);
        if (pending || locked)
        {
            Notify(context, TargetChange.Lost, target);
        }
    }

    internal PilotCallResult LockPassively(PilotCallContext context, long id) => Add(context, id, true);

    internal bool IsPending(long id) => _pending.ContainsKey(id);

    internal void CancelPassive(PilotCallContext context, long id) => Remove(context, id);

    internal PilotCallResult LockAutomatically(PilotCallContext context, long id) => Add(context, id);

    internal void ResetAutoMiningExclusions() => _miningExcludedTargets.Clear();

    internal bool IsExcludedFromAutoMining(long id) => _miningExcludedTargets.Contains(id);

    internal bool LockForAutoMining(PilotCallContext context, long id)
    {
        if (_locked.Contains(id) || _pending.ContainsKey(id))
        {
            return true;
        }
        if (Add(context, id) is not PilotCallResult.Success { Reply: TargetLockReply })
        {
            return false;
        }
        _miningLocks.Add(id);
        return true;
    }

    internal void ReleaseAutoMiningLocks(PilotCallContext context, Func<long, bool> keep)
    {
        foreach (long target in _miningLocks.Where(target => !keep(target)).ToArray())
        {
            Remove(context, target);
        }
    }

    private PilotCallResult Add(PilotCallContext context, long id, bool silent = false)
    {
        if (context.Flight.CanTarget && !context.Flight.CanAcquireTargets)
        {
            return new PilotCallResult.Failure("Wait for targeting systems to finish cloak recalibration.");
        }
        if (id == _shipId)
        {
            return new PilotCallResult.NamedFailure("DeniedTargetSelf", BlueEmpty.Dictionary);
        }
        if (FindTarget(context, id) is not { } target)
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
        if (!NpcEwarRules.CanTarget(Jamming(_shipId), id))
        {
            return new PilotCallResult.Failure("Electronic countermeasures prevent locking this target.");
        }
        var structure = context.Flight.StructureTargetingSource;
        var attributes = Attributes(context, structure);
        int limit = checked((int)attributes.GetValueOrDefault(DogmaAttributeId.MaxLockedTargets));
        if (_pending.Count + _locked.Count >= limit)
        {
            return new PilotCallResult.NamedFailure("TargetingSystemsAlreadyFullyUtilized", new TargetLimitErrorValues(limit));
        }
        if (Distance(context, structure, target) > attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange))
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
        PendingLock pending = new(clock.GetTimestamp(), seconds, silent);
        _pending.Add(id, pending);
        shared.Pending(_shipId, id);
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
                var result = Add(context, target);
                _miningLocks.Remove(target);
                _miningExcludedTargets.Remove(target);
                return result;
            case "RemoveTarget" or "CancelAddTarget" when args is [long target]:
                ExcludeAutoMiningTarget(target);
                Remove(context, target);
                return PilotCallResult.None;
            case "ClearTargets" when args.Length == 0:
                foreach (long id in _pending.Keys.Concat(_locked).ToArray())
                {
                    ExcludeAutoMiningTarget(id);
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

    private void ExcludeAutoMiningTarget(long id)
    {
        if (_miningLocks.Contains(id))
        {
            _miningExcludedTargets.Add(id);
        }
    }

    private IReadOnlyList<NpcEwarContact> Jamming(long ship) =>
        (npcEwar?.Incoming(ship) ?? [])
            .Concat(
                (electronicWarfare?.Incoming(ship) ?? [])
                    .Where(e => e.Module.Kind == NpcEwarKind.Jamming && e.Applied is not null)
                    .Select(e => new NpcEwarContact(
                        e.SourceShipId,
                        e.Module.TypeId,
                        ship,
                        new(
                            NpcEwarKind.Jamming,
                            e.Module.Effect,
                            e.Module.DurationMilliseconds,
                            e.Module.Optimal,
                            e.Module.Falloff,
                            1,
                            e.Applied!
                        ),
                        e.Applied!,
                        e.StartedAt,
                        e.ExpiresAt
                    ))
            )
            .ToArray();

    public void Reset()
    {
        if (_structureControl is null)
        {
            shared.RemoveShip(_shipId);
        }
        CancelPendingReplies();
        _pending.Clear();
        _locked.Clear();
        _miningLocks.Clear();
        _miningExcludedTargets.Clear();
        _targeters = [];
        _shipId = 0;
        _structureControl = null;
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
