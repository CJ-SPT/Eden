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
    TimeProvider clock,
    IPilotOverloading? overload = null
)
{
    private sealed record ActiveOverload(DogmaActiveEffect Effect, HeatCycleKey Key);

    private IReadOnlyList<FittedModuleCondition>? _previous;
    private readonly Dictionary<long, ActiveOverload> _overloaded = [];
    private long _ship;
    private readonly Dictionary<DogmaAttributeId, double> _heatAttributes = [];

    internal bool IsOverloaded(long module) => _overloaded.ContainsKey(module);

    internal void Selection(PilotCallContext context, IReadOnlyList<ModuleOverloadState> states, long now)
    {
        if (_ship != context.Pilot.ShipId)
        {
            Reset();
        }
        _ship = context.Pilot.ShipId;
        foreach (var state in states.Where(s => !s.Pending))
        {
            if (state.Requested)
            {
                _overloaded[state.ModuleId] = new(
                    new(state.ModuleId, context.Pilot.PilotId, _ship, null, (DogmaEffect)state.EffectId, now, null, 0),
                    new(state.ModuleId, now, long.MaxValue)
                );
            }
            else
            {
                _overloaded.Remove(state.ModuleId);
            }
        }
    }

    internal void Reset()
    {
        _previous = null;
        _overloaded.Clear();
        _ship = 0;
        _heatAttributes.Clear();
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
            _heatAttributes.Clear();
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
                    int firstNotice = context.Notifications.Count;
                    foreach (var source in effects)
                    {
                        source.CancelModule(context, module.Condition.ItemId);
                    }
                    if (module.BurnedAt is long stoppedAt)
                    {
                        for (int index = firstNotice; index < context.Notifications.Count; index++)
                        {
                            if (context.Notifications[index] is ShipEffectChanged { Active: false } stopped)
                            {
                                context.Notifications[index] = stopped with { StopTime = stoppedAt };
                            }
                        }
                    }
                    if (!module.Online && previous?.Online != false)
                    {
                        context.Notifications.Add(
                            new ShipEffectChanged(
                                new(module.Condition.ItemId, context.Pilot.PilotId, _ship, null, DogmaEffect.Online, now, 0, 0),
                                now,
                                false,
                                false,
                                module.BurnedAt ?? now
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
        ShipHeatSnapshot? state =
            current.Any(m => m.OverloadEffect > 0) || _overloaded.Count > 0 || _heatAttributes.Count > 0
                ? heat.Read(context.Pilot.PilotId, _ship, now)
                : null;
        var cycles = state?.Cycles ?? Array.Empty<PaidHeatCycle>();
        if (state is not null)
        {
            List<ModuleAttributeChange> changes = [];
            foreach (var rack in state.Racks)
            {
                var (temperature, absorption) = rack.Profile.Rack switch
                {
                    ModuleRack.High => (DogmaAttributeId.HeatHigh, DogmaAttributeId.HeatAbsorptionHigh),
                    ModuleRack.Medium => (DogmaAttributeId.HeatMedium, DogmaAttributeId.HeatAbsorptionMedium),
                    _ => (DogmaAttributeId.HeatLow, DogmaAttributeId.HeatAbsorptionLow),
                };
                Add(temperature, rack.Heat);
                Add(absorption, cycles.Where(c => c.Rack == rack.Profile.Rack).Sum(c => c.Absorption));
            }
            if (changes.Count > 0)
            {
                context.Notifications.Add(new ModuleAttributesChanged(changes));
            }
            void Add(DogmaAttributeId attribute, double value)
            {
                if (_heatAttributes.GetValueOrDefault(attribute) != value)
                {
                    changes.Add(new(context.Pilot.PilotId, new DogmaItemKey.Item(_ship), attribute, now, value));
                }
                _heatAttributes[attribute] = value;
            }
        }
        var requested = overload?.Requested(context.Pilot.PilotId, _ship);
        foreach (var (module, active) in _overloaded.ToArray())
        {
            var cycle = cycles.FirstOrDefault(c => c.ModuleId == module);
            if (cycle is not null)
            {
                _overloaded[module] = active with { Key = cycle.Key };
            }
            if (cycle is null && requested?.Contains(module) != true)
            {
                long stop = Math.Min(now, active.Key.CompletesAt);
                if (current.FirstOrDefault(m => m.Condition.ItemId == module)?.BurnedAt is long burnedAt)
                {
                    stop = Math.Min(stop, burnedAt);
                }
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
