using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Structures;

[Injectable(InjectionType.Scoped)]
internal sealed class StructureTargetingAdapter(IPilotStructureTargeting targeting)
{
    private StructureControlToken? _token;
    private StructureTargetingState? _state;
    private readonly Dictionary<long, TaskCompletionSource<PilotCallResult>> _replies = [];

    internal void Advance(PilotCallContext context)
    {
        var token = context.StructureControl?.Token;
        if (_token != token)
        {
            Forget(context);
            _token = token;
        }
        if (token is null)
        {
            return;
        }
        try
        {
            Apply(context, targeting.Read(token));
        }
        catch (InvalidDataException)
        {
            Forget(context);
            context.ReleaseStructureControl();
            _token = null;
        }
    }

    internal PilotCallResult Call(PilotCallContext context, string method, object?[] args, Dictionary<string, object?>? keywords)
    {
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected structure targeting arguments.");
        }
        Advance(context);
        if (_token is not { } token)
        {
            return new PilotCallResult.Failure("Take control of the structure before acquiring targets.");
        }
        try
        {
            switch (method)
            {
                case "AddTarget" when args is [long target]:
                    Apply(context, targeting.Add(token, target));
                    var scan = _state!.Targets.Single(item => item.TargetId == target);
                    if (scan.Locked)
                    {
                        return new TargetLockReply(false, Locked());
                    }
                    if (!_replies.TryGetValue(target, out var completion))
                    {
                        completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _replies.Add(target, completion);
                    }
                    return new TargetLockReply(true, Locked()) { Completion = completion.Task };
                case "RemoveTarget" or "CancelAddTarget" when args is [long target]:
                    Apply(context, targeting.Remove(token, target));
                    return PilotCallResult.None;
                case "ClearTargets" when args is []:
                    Apply(context, targeting.Clear(token));
                    return PilotCallResult.None;
                case "GetTargets" when args is []:
                    return new BlueList<long>(Locked());
                case "GetTargeters" when args is []:
                    return new BlueList<long>(_state?.Targeters ?? []);
                default:
                    return PilotCallResult.Unsupported;
            }
        }
        catch (Exception error) when (error is InvalidDataException or OverflowException)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private long[] Locked() => _state?.Targets.Where(target => target.Locked).Select(target => target.TargetId).ToArray() ?? [];

    private void Apply(PilotCallContext context, StructureTargetingState current)
    {
        foreach (var previous in _state?.Targets ?? [])
        {
            if (!current.Targets.Any(target => target.TargetId == previous.TargetId))
            {
                context.Notifications.Add(new TargetChanged(TargetChange.Lost, previous.TargetId));
                Cancel(previous.TargetId);
            }
        }
        var locked = current.Targets.Where(target => target.Locked).Select(target => target.TargetId).ToArray();
        foreach (long id in locked.Where(id => _state?.Targets.Any(target => target.TargetId == id && target.Locked) != true))
        {
            context.Notifications.Add(new TargetChanged(TargetChange.Added, id));
            if (_replies.Remove(id, out var reply))
            {
                reply.TrySetResult(new PilotCallResult.Success(new TargetLockReply(true, locked)));
            }
        }
        foreach (long id in (_state?.Targeters ?? []).Except(current.Targeters))
        {
            context.Notifications.Add(new TargetChanged(TargetChange.OtherLost, id));
        }
        foreach (long id in current.Targeters.Except(_state?.Targeters ?? []))
        {
            context.Notifications.Add(new TargetChanged(TargetChange.OtherAdded, id));
        }
        _state = current;
    }

    private void Forget(PilotCallContext context)
    {
        foreach (var target in _state?.Targets ?? [])
        {
            context.Notifications.Add(new TargetChanged(TargetChange.Lost, target.TargetId));
        }
        foreach (long id in _state?.Targeters ?? [])
        {
            context.Notifications.Add(new TargetChanged(TargetChange.OtherLost, id));
        }
        Reset();
    }

    internal void Reset()
    {
        foreach (long id in _replies.Keys.ToArray())
        {
            Cancel(id);
        }
        _state = null;
        _token = null;
    }

    private void Cancel(long id)
    {
        if (_replies.Remove(id, out var reply))
        {
            reply.TrySetResult(new PilotCallResult.NamedFailure("TargetingAttemptCancelled", BlueEmpty.Dictionary));
        }
    }
}
