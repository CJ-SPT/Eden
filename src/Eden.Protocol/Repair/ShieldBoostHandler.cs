using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class ShieldBoostHandler(TimeProvider clock, IPilotShieldBoosting boosting)
{
    private sealed record Cycle(long ShipId, ShieldBooster Booster, long Started, long StartTime, long Repeats, bool Stopping = false);
    private readonly Dictionary<long, Cycle> _cycles = [];

    public object?[]? ActiveEffect(DockedPilot pilot, long module) => _cycles.TryGetValue(module, out var cycle) && cycle.ShipId == pilot.ShipId
        ? [module, pilot.PilotId, pilot.ShipId, null, null, Array.Empty<object?>(), (int)DogmaEffect.ShieldBoosting,
            cycle.StartTime, cycle.Booster.DurationMilliseconds, cycle.Repeats] : null;

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
            if (clock.GetElapsedTime(cycle.Started).TotalMilliseconds < cycle.Booster.DurationMilliseconds)
            {
                continue;
            }
            Stop(context, cycle);
            if (cycle.Stopping || cycle.Repeats == 0)
            {
                continue;
            }
            try
            {
                Start(context, cycle.Booster.ItemId, cycle.Repeats - 1);
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                // A rejected next cycle never spends capacitor or grants health.
            }
        }
    }

    private void Start(PilotCallContext context, long module, long repeats)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var result = boosting.Boost(context.Pilot.PilotId, module, now);
        Cycle cycle = new(context.Pilot.ShipId, result.Booster, clock.GetTimestamp(), now, repeats);
        _cycles.Add(module, cycle);
        Effect(context, cycle, true);
        context.Flight.NotifyHealth(result.Health);
        context.Flight.SetShieldBoostEffect(Contact(cycle), true);
        context.Notifications.Add(("OnModuleAttributeChanges", [new List<object?>
        {
            Attribute(DogmaAttributeId.CapacitorCharge, result.Capacitor),
            Attribute(DogmaAttributeId.ShieldCharge, result.Health.Remaining.Shield),
        }]));

        object?[] Attribute(DogmaAttributeId attribute, double value) =>
            ["OnModuleAttributeChange", context.Pilot.PilotId, context.Pilot.ShipId, (int)attribute, now, value, null, now];
    }

    private static ShieldBoostContact Contact(Cycle cycle) => new(cycle.Booster.ItemId, cycle.Booster.TypeId, cycle.StartTime, cycle.Booster.DurationMilliseconds);

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (!_cycles.Remove(cycle.Booster.ItemId))
        {
            return;
        }
        Effect(context, cycle, false);
        context.Flight.SetShieldBoostEffect(Contact(cycle), false);
    }

    private void Effect(PilotCallContext context, Cycle cycle, bool active)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        context.Notifications.Add(("OnGodmaShipEffect", [cycle.Booster.ItemId, (int)DogmaEffect.ShieldBoosting, now, active, active,
            new object?[] { cycle.Booster.ItemId, context.Pilot.PilotId, cycle.ShipId, null, null, Array.Empty<object?>(), (int)DogmaEffect.ShieldBoosting },
            cycle.StartTime, cycle.Booster.DurationMilliseconds, cycle.Repeats, null, active ? null : now]));
    }

    public object? Call(PilotCallContext context, string service, string method, object?[] args)
    {
        if (service != "dogmaIM" || method is not ("Activate" or "Deactivate"))
        {
            return PilotCallRouter.Unsupported;
        }
        context.Flight.Checkpoint();
        Advance(context);
        try
        {
            if (method == "Activate" && args is [long module, var effect, var target, long repeats] &&
                BlueMarshal.Text(effect) == ShieldBoostDefinitions.EffectName && repeats is >= 0 and <= int.MaxValue)
            {
                if (!context.Flight.CanUseSelfModules || target is not null && !Equals(target, context.Pilot.ShipId))
                {
                    return new PilotCallRouter.Error("Activate the shield booster on your own ship in space.");
                }
                if (!_cycles.ContainsKey(module))
                {
                    Start(context, module, repeats);
                }
                return null;
            }
            if (method == "Deactivate" && args is [long item, var name] && BlueMarshal.Text(name) == ShieldBoostDefinitions.EffectName)
            {
                // Shields and capacitor are applied at cycle start; finish the
                // paid cycle but do not begin another one after a manual stop.
                if (_cycles.TryGetValue(item, out var cycle))
                {
                    _cycles[item] = cycle with { Stopping = true };
                }
                return null;
            }
            return PilotCallRouter.Unsupported;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallRouter.Error(error.Message);
        }
        catch (OverflowException)
        {
            return new PilotCallRouter.Error("The shield boost exceeded the local cycle limit.");
        }
    }
}
