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
internal sealed class PropulsionHandler(TimeProvider clock, IPilotPropulsion propulsion) : IModuleEffectSource, IDisposable
{
    private sealed record Cycle(
        long PilotId,
        long ShipId,
        PropulsionModule Module,
        long Started,
        long StartTime,
        long Repeats,
        bool Stopping = false
    );

    private Cycle? _cycle;

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module) =>
        _cycle is { } cycle && cycle.ShipId == pilot.ShipId && cycle.Module.ItemId == module
            ? new(
                module,
                pilot.PilotId,
                pilot.ShipId,
                null,
                cycle.Module.Effect,
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
                Start(context, cycle.Module.ItemId, cycle.Repeats - 1, cycle.Module.Effect);
                return;
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                // An unpaid next cycle adds neither speed nor mass.
            }
        }
        Stop(context, cycle);
    }

    private void Start(PilotCallContext context, long module, long repeats, DogmaEffect effect)
    {
        long started = clock.GetTimestamp();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = propulsion.Activate(context.Pilot.PilotId, module, now, effect);
        Cycle cycle = new(context.Pilot.PilotId, context.Pilot.ShipId, result.Module, started, now, repeats);
        bool continuing = _cycle is not null;
        _cycle = cycle;
        context.Flight.SetPropulsionEffect(new(result.Module, now));
        if (!continuing)
        {
            // IdleRamps clears the native HUD phase. A paid automatic repeat
            // continues that effect instead of stopping and restarting its timer.
            Effect(context, cycle, true);
        }
        NotifyShipAttributes(context, result.Capacitor, result.SignatureRadius);
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        double signature = propulsion.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
        _cycle = null;
        context.Flight.SetPropulsionEffect(null);
        Effect(context, cycle, false);
        if (cycle.ShipId == context.Pilot.ShipId)
        {
            NotifyShipAttributes(context, signature: signature);
        }
    }

    private void NotifyShipAttributes(PilotCallContext context, double? capacitor = null, double? signature = null)
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
        double radius = signature ?? propulsion.SignatureRadius(context.Pilot.PilotId);
        if (radius > 0)
        {
            changes.Add(Attribute(DogmaAttributeId.SignatureRadius, radius));
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
                    cycle.PilotId,
                    cycle.ShipId,
                    null,
                    cycle.Module.Effect,
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
            var requested = PropulsionWireDefinitions.Effect(BlueMarshal.Text(args.ElementAtOrDefault(1)));
            if (requested is null)
            {
                return PilotCallResult.Unsupported;
            }
            if (method == "Activate" && args is [long module, _, var target, long repeats] && repeats is >= 0 and <= int.MaxValue)
            {
                if (!context.Flight.CanTarget || target is not null && !Equals(target, context.Pilot.ShipId))
                {
                    return new PilotCallResult.Failure("Activate the propulsion module on your own ship outside warp.");
                }
                if (_cycle is { } current)
                {
                    return current.Module.ItemId == module && current.Module.Effect == requested
                        ? PilotCallResult.None
                        : new PilotCallResult.Failure("Only one propulsion module can run at a time.");
                }
                Start(context, module, repeats, requested.Value);
                return PilotCallResult.None;
            }
            if (method == "Deactivate" && args is [long item, _])
            {
                if (_cycle is { } current && current.Module.ItemId == item && current.Module.Effect == requested)
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
            return new PilotCallResult.Failure("The propulsion module exceeded the local cycle limit.");
        }
    }

    public void Dispose()
    {
        if (_cycle is { } cycle)
        {
            _ = propulsion.Stop(cycle.PilotId, cycle.ShipId, cycle.Module.ItemId);
            _cycle = null;
        }
    }
}
