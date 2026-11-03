using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Propulsion;

[Injectable(InjectionType.Scoped)]
internal sealed class PropulsionHandler(TimeProvider clock, IPilotPropulsion propulsion) : IModuleEffectSource
{
    private sealed record Cycle(long ShipId, Afterburner Module, long Started, long StartTime, long Repeats, bool Stopping = false);

    private Cycle? _cycle;

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycle is { } cycle && cycle.ShipId == pilot.ShipId && cycle.Module.ItemId == module
            ? new(
                module,
                pilot.PilotId,
                pilot.ShipId,
                null,
                DogmaEffect.Afterburner,
                cycle.StartTime,
                cycle.Module.DurationMilliseconds,
                cycle.Repeats
            )
            : null;

    public void RefreshNavigation(PilotCallContext context)
    {
        double speed = propulsion.MaxSpeed(context.Pilot.PilotId);
        if ((float)speed == context.Flight.BaseSpeed)
        {
            return;
        }
        context.Flight.SetBaseSpeed(speed);
        NotifyShipAttributes(context);
    }

    public void Cancel(PilotCallContext context)
    {
        if (_cycle is { } cycle)
        {
            Stop(context, cycle);
        }
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected || _cycle is not { } cycle)
        {
            return;
        }
        if (cycle.ShipId != context.Pilot.ShipId || !context.Flight.CanUseSelfModules)
        {
            Stop(context, cycle);
            return;
        }
        if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Module.DurationMilliseconds)
        {
            return;
        }
        if (!cycle.Stopping && cycle.Repeats > 0 && context.Flight.CanTarget)
        {
            try
            {
                // Keep flight properties in place across paid repeat cycles.
                // Sending a temporary base speed makes the native ball decelerate.
                Start(context, cycle.Module.ItemId, cycle.Repeats - 1);
                return;
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                // An unpaid next cycle adds neither speed nor mass.
            }
        }
        Stop(context, cycle);
    }

    private void Start(PilotCallContext context, long module, long repeats)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = propulsion.Activate(context.Pilot.PilotId, module, now);
        Cycle cycle = new(context.Pilot.ShipId, result.Module, clock.GetTimestamp(), now, repeats);
        if (_cycle is { } previous)
        {
            Effect(context, previous, false);
        }
        _cycle = cycle;
        context.Flight.SetPropulsionEffect(new(result.Module, now));
        Effect(context, cycle, true);
        NotifyShipAttributes(context, result.Capacitor);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        _cycle = null;
        context.Flight.SetPropulsionEffect(null);
        Effect(context, cycle, false);
        NotifyShipAttributes(context);
    }

    private void NotifyShipAttributes(PilotCallContext context, double? capacitor = null)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        List<ModuleAttributeChange> changes =
        [
            Attribute(DogmaAttributeId.MaxVelocity, context.Pilot.Flight.MaxSpeed),
            Attribute(DogmaAttributeId.Mass, context.Pilot.Flight.Mass),
        ];
        if (capacitor.HasValue)
        {
            changes.Add(Attribute(DogmaAttributeId.CapacitorCharge, capacitor.Value));
        }
        context.Notifications.Add(new ModuleAttributesChanged(changes));
        ModuleAttributeChange Attribute(DogmaAttributeId attribute, double value) =>
            new(context.Pilot.PilotId, new DogmaItemKey.Item(context.Pilot.ShipId), attribute, now, value);
    }

    private void Effect(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        context.Notifications.Add(
            new ShipEffectChanged(
                new DogmaActiveEffect(
                    cycle.Module.ItemId,
                    context.Pilot.PilotId,
                    cycle.ShipId,
                    null,
                    DogmaEffect.Afterburner,
                    cycle.StartTime,
                    cycle.Module.DurationMilliseconds,
                    cycle.Repeats
                ),
                now,
                active,
                active,
                active ? null : now
            )
        );
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
                && BlueMarshal.Text(effect) == PropulsionWireDefinitions.EffectName
                && repeats is >= 0 and <= int.MaxValue
            )
            {
                if (!context.Flight.CanTarget || target is not null && !Equals(target, context.Pilot.ShipId))
                {
                    return new PilotCallResult.Failure("Activate the afterburner on your own ship outside warp.");
                }
                if (_cycle is { } current)
                {
                    return current.Module.ItemId == module
                        ? PilotCallResult.None
                        : new PilotCallResult.Failure("Only one afterburner can run at a time.");
                }
                Start(context, module, repeats);
                return PilotCallResult.None;
            }
            if (
                method == "Deactivate"
                && args is [long item, var effectName]
                && BlueMarshal.Text(effectName) == PropulsionWireDefinitions.EffectName
            )
            {
                if (_cycle is { } current && current.Module.ItemId == item)
                {
                    _cycle = current with { Stopping = true };
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
            return new PilotCallResult.Failure("The afterburner exceeded the local cycle limit.");
        }
    }
}
