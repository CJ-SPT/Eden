using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class PlayerEwarHandler(
    TimeProvider clock,
    IPilotElectronicWarfare warfare,
    TargetingHandler targeting,
    SupportModuleWire attributes
) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(
        long PilotId,
        long ShipId,
        ElectronicWarfareModule Module,
        TackleTarget? Target,
        long Started,
        long StartTime,
        long Repeats,
        bool Stopping = false
    );

    private readonly Dictionary<long, Cycle> _cycles = [];
    private ElectronicWarfareContact[] _received = [];

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    private static DogmaActiveEffect Effect(Cycle cycle) =>
        new(
            cycle.Module.ItemId,
            cycle.PilotId,
            cycle.ShipId,
            cycle.Target?.Id,
            cycle.Module.Effect,
            cycle.StartTime,
            cycle.Module.DurationMilliseconds,
            cycle.Repeats
        );

    private static ElectronicWarfareSpaceContact Contact(Cycle cycle) =>
        new(cycle.Module, cycle.Target?.Id ?? cycle.ShipId, cycle.StartTime);

    public void CancelModule(PilotCallContext context, long module)
    {
        if (_cycles.TryGetValue(module, out var cycle))
        {
            Stop(context, cycle);
        }
    }

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId ? Effect(cycle) : null;

    private void Notify(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(new ShipEffectChanged(Effect(cycle), Now(), active, active, active ? null : Now()));

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long? targetId, long repeats, Cycle? previous = null)
    {
        if (targetId is not long id || context.Flight.FindTarget(id) is null)
        {
            throw new InvalidDataException("Lock a nearby ship or NPC before applying electronic warfare.");
        }
        TackleTarget target = new(id, context.Flight.VisibleNpcs.Any(n => n.Id == id) ? TackleTargetKind.Npc : TackleTargetKind.Ship);
        var locked = target is not null && targeting.IsLocked(target.Id) ? new HashSet<long> { target.Id } : [];
        long started = clock.GetTimestamp();
        long time = Now();
        var result = warfare.Activate(context.Pilot.PilotId, module, effect, target, locked, time);
        Cycle cycle = new(context.Pilot.PilotId, context.Pilot.ShipId, result.Module, result.Target, started, time, repeats);
        _cycles[module] = cycle;
        if (previous is null)
        {
            Notify(context, cycle, true);
        }
        else
        {
            context.Flight.SetElectronicWarfareEffect(Contact(previous), false);
        }
        context.Flight.SetElectronicWarfareEffect(Contact(cycle), true);
        attributes.Refresh(context, result.Capacitor);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        warfare.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        _cycles.Remove(cycle.Module.ItemId);
        Notify(context, cycle, false);
        if (cycle.ShipId == context.Pilot.ShipId)
        {
            context.Flight.SetElectronicWarfareEffect(Contact(cycle), false);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        // The source controller owns paid intervals and lifecycle cancellation.
        // Observer ticks read committed resident contacts without SQL sweeps.
        foreach (var cycle in _cycles.Values.ToArray())
        {
            bool online = warfare.IsOnline(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
            if (
                cycle.ShipId != context.Pilot.ShipId
                || !context.Flight.CanTarget
                || !online
                || cycle.Target is { } target && (!targeting.IsLocked(target.Id) || context.Flight.FindTarget(target.Id) is null)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Module.DurationMilliseconds)
            {
                continue;
            }
            if (!context.Flight.Cloaked && !cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, cycle.Module.ItemId, cycle.Module.Effect, cycle.Target?.Id, cycle.Repeats - 1, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
        var incoming = context.Flight.CanUseSelfModules
            ? warfare.Incoming(context.Pilot.ShipId).Where(e => e.Applied is not null).ToArray()
            : [];
        var ended = _received.Where(e => !incoming.Any(n => SameSource(e, n))).ToArray();
        var added = incoming.Where(e => !_received.Any(n => SameSource(e, n))).ToArray();
        foreach (var contact in ended)
        {
            context.NotificationsBeforeFlight.Add(new PlayerEwarChanged(contact, false));
        }
        foreach (var contact in added)
        {
            context.NotificationsBeforeFlight.Add(new PlayerEwarChanged(contact, true));
        }
        foreach (var contact in _received.Where(e => e.Module.Effect == DogmaEffect.PlayerEcm && !incoming.Contains(e)))
        {
            context.NotificationsBeforeFlight.Add(new PlayerJamChanged(contact, false));
        }
        foreach (var contact in incoming.Where(e => e.Module.Effect == DogmaEffect.PlayerEcm && !_received.Contains(e)))
        {
            context.NotificationsBeforeFlight.Add(new PlayerJamChanged(contact, true));
        }
        if (!_received.SequenceEqual(incoming))
        {
            attributes.Refresh(context);
            context.Flight.RefreshNpcs();
        }
        _received = incoming;
    }

    private static bool SameSource(ElectronicWarfareContact first, ElectronicWarfareContact second) =>
        first.Module.ItemId == second.Module.ItemId && first.Target == second.Target;

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
        foreach (var contact in _received)
        {
            context.NotificationsBeforeFlight.Add(new PlayerEwarChanged(contact, false));
            if (contact.Module.Effect == DogmaEffect.PlayerEcm)
            {
                context.NotificationsBeforeFlight.Add(new PlayerJamChanged(contact, false));
            }
        }
        _received = [];
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || args.Length < 2 || PlayerEwarWire.Effect(BlueMarshal.Text(args[1])) is not { } effect)
        {
            return PilotCallResult.Unsupported;
        }
        bool valid = (method, args) switch
        {
            ("Activate", [long, _, null or long, long repeats]) when repeats is >= 0 and <= int.MaxValue => true,
            ("Deactivate", [long, _]) => true,
            _ => false,
        };
        if (!valid)
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            context.Flight.Checkpoint();
            targeting.Advance(context);
            Advance(context);
            long module = (long)args[0]!;
            if (_cycles.TryGetValue(module, out var cycle))
            {
                if (cycle.Module.Effect != effect || method == "Activate" && args[2] is long id && id != (cycle.Target?.Id ?? cycle.ShipId))
                {
                    return new PilotCallResult.Failure("The module is already active with a different effect or target.");
                }
                if (method == "Deactivate")
                {
                    _cycles[module] = cycle with { Stopping = true };
                }
                return PilotCallResult.None;
            }
            if (method == "Deactivate")
            {
                return PilotCallResult.None;
            }
            if (!context.Flight.CanTarget || context.Flight.InWarp)
            {
                return new PilotCallResult.Failure("Activate electronic warfare modules while in space and outside warp.");
            }
            Start(context, module, effect, args[2] as long?, (long)args[3]!);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The electronic warfare module exceeded the local cycle limit.");
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _cycles.Values)
        {
            warfare.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        }
        _cycles.Clear();
    }
}
