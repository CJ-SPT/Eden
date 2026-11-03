using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Ammunition;

[Injectable(InjectionType.Scoped)]
internal sealed class CapacitorBoosterHandler(TimeProvider clock, IPilotCapacitorBoosting boosting) : IModuleEffectSource
{
    // Build 3662168 effect 48 has no spatial effect guid. Godma drives its HUD.
    internal const string EffectName = "powerBooster";

    private sealed record Cycle(long ShipId, CapacitorBooster Module, long Started, long StartTime, long Repeats, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId ? Effect(pilot.PilotId, cycle) : null;

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        foreach (var cycle in _cycles.Values.ToArray())
        {
            if (cycle.ShipId != context.Pilot.ShipId || !context.Flight.CanUseSelfModules)
            {
                Stop(context, cycle);
                continue;
            }
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Module.DurationMilliseconds)
            {
                continue;
            }
            if (!cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, cycle.Module.ItemId, cycle.Repeats - 1, true);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException)
                {
                    // No compatible charge means no additional injection.
                }
            }
            Stop(context, cycle);
        }
    }

    private void Start(PilotCallContext context, long module, long repeats, bool continuing = false)
    {
        long started = clock.GetTimestamp();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = boosting.Inject(context.Pilot.PilotId, module, now);
        Cycle cycle = new(context.Pilot.ShipId, result.Module, started, now, repeats);
        _cycles[module] = cycle;
        if (!continuing)
        {
            NotifyEffect(context, cycle, true);
        }
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    now,
                    result.Capacitor
                ),
            ])
        );
        MagazineWire.NotifyQuantity(context, result.Magazine, result.Magazine.Quantity, now);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (_cycles.Remove(cycle.Module.ItemId))
        {
            NotifyEffect(context, cycle, false);
        }
    }

    private static DogmaActiveEffect Effect(long pilot, Cycle cycle) =>
        new(
            cycle.Module.ItemId,
            pilot,
            cycle.ShipId,
            null,
            DogmaEffect.CapacitorBoosting,
            cycle.StartTime,
            cycle.Module.DurationMilliseconds,
            cycle.Repeats
        );

    private void NotifyEffect(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        context.Notifications.Add(new ShipEffectChanged(Effect(context.Pilot.PilotId, cycle), now, active, active, active ? null : now));
    }

    public PilotCallResult Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate"))
        {
            return PilotCallResult.Unsupported;
        }
        context.Flight.Checkpoint();
        Advance(context);
        try
        {
            if (
                method == "Activate"
                && args is [long module, var effect, var target, long repeats]
                && BlueMarshal.Text(effect) == EffectName
                && repeats is >= 0 and <= int.MaxValue
            )
            {
                if (!context.Flight.CanUseSelfModules || target is not null && !Equals(target, context.Pilot.ShipId))
                {
                    return new PilotCallResult.Failure("Activate the capacitor booster on your own ship in space.");
                }
                if (!_cycles.ContainsKey(module))
                {
                    Start(context, module, repeats);
                }
                return PilotCallResult.None;
            }
            if (method == "Deactivate" && args is [long moduleId, var name] && BlueMarshal.Text(name) == EffectName)
            {
                if (_cycles.TryGetValue(moduleId, out var cycle))
                {
                    _cycles[moduleId] = cycle with { Stopping = true };
                }
                return PilotCallResult.None;
            }
            return PilotCallResult.Unsupported;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallResult.Failure("The capacitor boost exceeded the local cycle limit.");
        }
    }
}
