using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Targeting;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Scoped)]
internal sealed class CapacitorWarfareHandler(
    TimeProvider clock,
    IPilotCapacitorWarfare warfare,
    TargetingHandler targeting,
    CapacitorSignal signal,
    IPilotShipResources resources
) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(long PilotId, CapacitorCycle Effect, long Repeats, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private IReadOnlyList<CapacitorCycle> _incoming = [];
    private readonly HashSet<long> _dirtyShips = [];
    private long _pilotId;
    private bool _subscribed;

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal void Enter(PilotCallContext context)
    {
        lock (_dirtyShips)
        {
            _pilotId = context.Pilot.PilotId;
        }
        if (!_subscribed)
        {
            signal.Changed += Changed;
            _subscribed = true;
        }
    }

    private void Changed(CapacitorChanged change)
    {
        lock (_dirtyShips)
        {
            if (change.PilotId == _pilotId)
            {
                _dirtyShips.Add(change.ShipId);
            }
        }
    }

    private static DogmaActiveEffect Active(Cycle cycle) =>
        new(
            cycle.Effect.Module.ItemId,
            cycle.PilotId,
            cycle.Effect.SourceShipId,
            cycle.Effect.TargetId,
            cycle.Effect.Module.Effect,
            cycle.Effect.StartedAt,
            cycle.Effect.Module.DurationMilliseconds,
            cycle.Repeats
        );

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.Effect.SourceShipId == pilot.ShipId ? Active(cycle) : null;

    private void Start(PilotCallContext context, long module, DogmaEffect effect, long target, long repeats, Cycle? previous = null)
    {
        if (context.Flight.FindTarget(target) is null)
        {
            throw new InvalidDataException("Lock another nearby pilot's ship before using a capacitor module.");
        }
        var result = warfare.Activate(
            context.Pilot.PilotId,
            module,
            effect,
            target,
            targeting.IsLocked(target) ? new HashSet<long> { target } : [],
            Now()
        );
        Cycle cycle = new(context.Pilot.PilotId, result.Cycle, repeats);
        if (previous is not null)
        {
            context.Flight.SetCapacitorEffect(previous.Effect, false);
        }
        else
        {
            context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now(), true, true));
        }
        _cycles[module] = cycle;
        signal.Set(result.Cycle, true);
        context.Flight.SetCapacitorEffect(result.Cycle, true);
        FlushCapacitor(context);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        warfare.Stop(cycle.PilotId, cycle.Effect.SourceShipId, cycle.Effect.Module.ItemId);
        signal.Set(cycle.Effect, false);
        _cycles.Remove(cycle.Effect.Module.ItemId);
        context.Notifications.Add(new ShipEffectChanged(Active(cycle), Now(), false, false, Now()));
        if (context.Pilot.ShipId == cycle.Effect.SourceShipId)
        {
            context.Flight.SetCapacitorEffect(cycle.Effect, false);
        }
    }

    private void FlushCapacitor(PilotCallContext context)
    {
        bool dirty;
        lock (_dirtyShips)
        {
            dirty = _dirtyShips.Remove(context.Pilot.ShipId);
            _dirtyShips.Clear();
        }
        if (dirty)
        {
            context.Notifications.Add(
                new ModuleAttributesChanged([
                    new(
                        context.Pilot.PilotId,
                        new DogmaItemKey.Item(context.Pilot.ShipId),
                        DogmaAttributeId.CapacitorCharge,
                        Now(),
                        resources.Capacitor(context.Pilot.PilotId, Now())
                    ),
                ])
            );
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        Enter(context);
        foreach (var cycle in _cycles.Values.ToArray())
        {
            var effect = cycle.Effect;
            if (
                effect.SourceShipId != context.Pilot.ShipId
                || !context.Flight.CanTarget
                || context.Flight.InWarp
                || !targeting.IsLocked(effect.TargetId)
                || context.Flight.FindTarget(effect.TargetId) is null
                || !warfare.IsOnline(cycle.PilotId, effect.SourceShipId, effect.Module.ItemId)
            )
            {
                Stop(context, cycle);
                continue;
            }
            if (effect.ExpiresAt > Now())
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, effect.Module.ItemId, effect.Module.Effect, effect.TargetId, cycle.Repeats - 1, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
        var incoming = context.Flight.CanTarget ? signal.Incoming(context.Pilot.ShipId, Now()) : [];
        if (!ReferenceEquals(incoming, _incoming))
        {
            foreach (var ended in _incoming.Where(e => !incoming.Any(c => c.Module.ItemId == e.Module.ItemId)))
            {
                context.NotificationsBeforeFlight.Add(new CapacitorEwarChanged(ended, false));
            }
            foreach (var added in incoming.Where(e => !_incoming.Any(c => c.Module.ItemId == e.Module.ItemId)))
            {
                context.NotificationsBeforeFlight.Add(new CapacitorEwarChanged(added, true));
            }
            _incoming = incoming;
        }
        FlushCapacitor(context);
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
        foreach (var cycle in _incoming)
        {
            context.NotificationsBeforeFlight.Add(new CapacitorEwarChanged(cycle, false));
        }
        _incoming = [];
        Dispose();
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || args.Length < 2 || CapacitorWire.Effect(BlueMarshal.Text(args[1])) is not { } effect)
        {
            return PilotCallResult.Unsupported;
        }
        bool valid = (method, args) switch
        {
            ("Activate", [long, _, long, long repeats]) when repeats is >= 0 and <= int.MaxValue => true,
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
                if (cycle.Effect.Module.Effect != effect || method == "Activate" && (long)args[2]! != cycle.Effect.TargetId)
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
                return new PilotCallResult.Failure("Activate capacitor modules in space and outside warp.");
            }
            Start(context, module, effect, (long)args[2]!, (long)args[3]!);
            return PilotCallResult.None;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The capacitor module exceeded the local cycle limit.");
        }
    }

    public void Dispose()
    {
        foreach (var cycle in _cycles.Values)
        {
            warfare.Stop(cycle.PilotId, cycle.Effect.SourceShipId, cycle.Effect.Module.ItemId);
            signal.Set(cycle.Effect, false);
        }
        _cycles.Clear();
        if (_subscribed)
        {
            signal.Changed -= Changed;
            _subscribed = false;
        }
        lock (_dirtyShips)
        {
            _dirtyShips.Clear();
        }
    }
}
