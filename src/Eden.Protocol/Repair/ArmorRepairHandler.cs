using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class ArmorRepairHandler(TimeProvider clock, IPilotArmorRepairing repairing) : IModuleEffectSource
{
    private sealed record Cycle(long ShipId, ArmorRepairSchedule Schedule, long Repeats, bool Stopping = false);

    private readonly Dictionary<long, Cycle> _cycles = [];
    private long _restoredShip;

    public DogmaActiveEffect? ActiveEffect(DockedPilot pilot, long module)
    {
        var schedule = _cycles.GetValueOrDefault(module)?.Schedule;
        if (schedule is null && pilot.Position is not null)
        {
            schedule = repairing.Read(pilot.PilotId, pilot.ShipId, Now()).SingleOrDefault(s => s.ItemId == module);
        }
        return schedule is null ? null : Effect(pilot.PilotId, pilot.ShipId, schedule, _cycles.GetValueOrDefault(module)?.Repeats ?? 0);
    }

    public void Enter(PilotCallContext context)
    {
        if (!context.Selected || !context.Flight.CanUseSelfModules || _restoredShip == context.Pilot.ShipId)
        {
            return;
        }
        Cancel(context);
        _restoredShip = context.Pilot.ShipId;
        foreach (var schedule in repairing.Read(context.Pilot.PilotId, context.Pilot.ShipId, Now()))
        {
            Cycle cycle = new(context.Pilot.ShipId, schedule, 0);
            _cycles.Add(schedule.ItemId, cycle);
            NotifyEffect(context, cycle, true);
            context.Flight.SetArmorRepairEffect(Contact(cycle), true);
        }
    }

    public void Cancel(PilotCallContext context)
    {
        foreach (var cycle in _cycles.Values.ToArray())
        {
            Stop(context, cycle);
        }
        _restoredShip = 0;
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        if (!context.Flight.CanUseSelfModules)
        {
            Cancel(context);
            return;
        }
        Enter(context);
        foreach (var cycle in _cycles.Values.ToArray())
        {
            if (cycle.Schedule.CompletesAt > Now())
            {
                continue;
            }
            var health = repairing.Complete(context.Pilot.PilotId, cycle.ShipId, Now());
            NotifyHealth(context, health);
            if (!cycle.Stopping && cycle.Repeats > 0)
            {
                try
                {
                    Start(context, cycle.Schedule.ItemId, cycle.Repeats - 1, cycle);
                    continue;
                }
                catch (Exception error) when (error is InvalidDataException or OverflowException) { }
            }
            Stop(context, cycle);
        }
    }

    private void Start(PilotCallContext context, long module, long repeats, Cycle? previous = null)
    {
        var result = repairing.Start(context.Pilot.PilotId, module, Now());
        Cycle cycle = new(context.Pilot.ShipId, result.Schedule, repeats);
        _cycles[module] = cycle;
        if (previous is null)
        {
            NotifyEffect(context, cycle, true);
        }
        else
        {
            context.Flight.SetArmorRepairEffect(Contact(previous), false);
        }
        context.Flight.SetArmorRepairEffect(Contact(cycle), true);
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(context.Pilot.ShipId),
                    DogmaAttributeId.CapacitorCharge,
                    Now(),
                    result.Capacitor
                ),
            ])
        );
    }

    private void NotifyHealth(PilotCallContext context, ShipHealth health)
    {
        context.Flight.NotifyHealth(health);
        context.Notifications.Add(
            new ModuleAttributesChanged([
                new(
                    context.Pilot.PilotId,
                    new DogmaItemKey.Item(health.ShipId),
                    DogmaAttributeId.ArmorDamage,
                    Now(),
                    health.Defense.Armor - health.Remaining.Armor
                ),
            ])
        );
    }

    private void Stop(PilotCallContext context, Cycle cycle)
    {
        if (_cycles.Remove(cycle.Schedule.ItemId))
        {
            NotifyEffect(context, cycle, false);
            context.Flight.SetArmorRepairEffect(Contact(cycle), false);
        }
    }

    private static ArmorRepairContact Contact(Cycle cycle) =>
        new(cycle.Schedule.ItemId, cycle.Schedule.TypeId, cycle.Schedule.StartedAt, cycle.Schedule.DurationMilliseconds);

    private static DogmaActiveEffect Effect(long pilot, long ship, ArmorRepairSchedule schedule, long repeats) =>
        new(schedule.ItemId, pilot, ship, null, DogmaEffect.ArmorRepair, schedule.StartedAt, schedule.DurationMilliseconds, repeats);

    private void NotifyEffect(PilotCallContext context, Cycle cycle, bool active) =>
        context.Notifications.Add(
            new ShipEffectChanged(
                Effect(context.Pilot.PilotId, cycle.ShipId, cycle.Schedule, cycle.Repeats),
                Now(),
                active,
                active,
                active ? null : Now()
            )
        );

    private long Now() => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

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
                && BlueMarshal.Text(effect) == ArmorRepairWireDefinitions.EffectName
                && repeats is >= 0 and <= int.MaxValue
            )
            {
                if (!context.Flight.CanUseSelfModules || target is not null && !Equals(target, context.Pilot.ShipId))
                {
                    return new PilotCallResult.Failure("Activate the armor repairer on your own ship in space.");
                }
                if (!_cycles.ContainsKey(module))
                {
                    Start(context, module, repeats);
                }
                return PilotCallResult.None;
            }
            if (
                method == "Deactivate"
                && args is [long moduleId, var name]
                && BlueMarshal.Text(name) == ArmorRepairWireDefinitions.EffectName
            )
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
            return new PilotCallResult.Failure("The armor repair exceeded the local cycle limit.");
        }
    }
}
