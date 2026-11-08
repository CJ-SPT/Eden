using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Repair;

[Injectable(InjectionType.Scoped)]
internal sealed class ModuleBurnoutMonitor(
    IPilotModuleConditions conditions,
    IPilotShipHeat heat,
    IEnumerable<IModuleEffectSource> effects,
    TimeProvider clock
)
{
    private sealed record ActiveOverload(DogmaActiveEffect Effect, HeatCycleKey Key);

    private IReadOnlyList<FittedModuleCondition>? _previous;
    private readonly Dictionary<long, ActiveOverload> _overloaded = [];
    private long _ship;

    internal void Reset()
    {
        _previous = null;
        _overloaded.Clear();
        _ship = 0;
    }

    internal void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            return;
        }
        if (_ship != context.Pilot.ShipId)
        {
            _previous = null;
            _overloaded.Clear();
            _ship = context.Pilot.ShipId;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var current = conditions.Read(context.Pilot.PilotId, _ship);
        if (!ReferenceEquals(current, _previous))
        {
            foreach (var module in current)
            {
                var previous = _previous?.FirstOrDefault(m => m.Condition.ItemId == module.Condition.ItemId);
                if (module.Condition.BurnedOut)
                {
                    foreach (var source in effects)
                    {
                        source.CancelModule(context, module.Condition.ItemId);
                    }
                    if (previous?.Online != false)
                    {
                        context.Notifications.Add(
                            new ShipEffectChanged(
                                new(module.Condition.ItemId, context.Pilot.PilotId, _ship, null, DogmaEffect.Online, now, 0, 0),
                                now,
                                false,
                                false,
                                now
                            )
                        );
                    }
                }
                if (module.Condition.Damage > (previous?.Condition.Damage ?? 0))
                {
                    context.Notifications.Add(
                        new ModuleAttributesChanged([
                            new(
                                context.Pilot.PilotId,
                                new DogmaItemKey.Item(module.Condition.ItemId),
                                DogmaAttributeId.Damage,
                                now,
                                module.Condition.Damage
                            ),
                        ])
                    );
                }
            }
            _previous = current;
        }
        var cycles =
            current.Any(m => m.OverloadEffect > 0) || _overloaded.Count > 0
                ? heat.Read(context.Pilot.PilotId, _ship, now).Cycles
                : Array.Empty<PaidHeatCycle>();
        foreach (var (module, active) in _overloaded.ToArray())
        {
            if (!cycles.Any(c => c.ModuleId == module && c.Key == active.Key))
            {
                long stop = Math.Min(now, active.Key.CompletesAt);
                context.Notifications.Add(new ShipEffectChanged(active.Effect, now, false, false, stop));
                _overloaded.Remove(module);
            }
        }
        foreach (var cycle in cycles)
        {
            if (
                !_overloaded.ContainsKey(cycle.ModuleId)
                && current.FirstOrDefault(m => m.Condition.ItemId == cycle.ModuleId) is { Online: true, OverloadEffect: > 0 } module
            )
            {
                DogmaActiveEffect active = new(
                    cycle.ModuleId,
                    context.Pilot.PilotId,
                    _ship,
                    null,
                    (DogmaEffect)module.OverloadEffect,
                    cycle.StartedAt,
                    (cycle.CompletesAt - cycle.StartedAt) / (double)TimeSpan.TicksPerMillisecond,
                    0
                );
                context.Notifications.Add(new ShipEffectChanged(active, now, true, true));
                _overloaded.Add(cycle.ModuleId, new(active, cycle.Key));
            }
        }
    }
}
