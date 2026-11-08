using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Thermal;

public sealed record HeatDamageModule(long ItemId, ModuleRack Rack, int Slot, bool Online, ModuleCondition Condition, double Damage)
{
    public long? BurnedAt { get; init; }
}

public static class HeatDamageRules
{
    private const double Percent = 100;

    public static double Probability(double heat, int online, int slots, double attenuation, int distance)
    {
        if (
            !double.IsFinite(heat)
            || heat < 0
            || online < 0
            || slots <= 0
            || online > slots
            || !double.IsFinite(attenuation)
            || attenuation is < 0 or > 1
            || distance < 0
        )
        {
            throw new InvalidDataException("Invalid heat damage probability inputs.");
        }
        return Math.Clamp(heat / Percent * online / slots * Math.Pow(attenuation, distance), 0, 1);
    }

    public static ShipHeatSnapshot Advance(ShipHeatSnapshot state, long now)
    {
        while (state.Cycles.Any(c => c.CompletesAt <= now))
        {
            var step = HeatCycleRules.AdvanceToNextDeadline(state, now);
            var modules = state.Modules.ToDictionary(m => m.ItemId);
            foreach (var completion in step.Completed)
            {
                if (!modules.TryGetValue(completion.Cycle.ModuleId, out var source) || !source.Online || source.Condition.BurnedOut)
                {
                    continue;
                }
                var rack = step.State.Racks.Single(r => r.Profile.Rack == source.Rack);
                int online = modules.Values.Count(m => m.Online && !m.Condition.BurnedOut);
                foreach (
                    var target in modules.Values.Where(m => m.Rack == source.Rack && !m.Condition.BurnedOut).OrderBy(m => m.Slot).ToArray()
                )
                {
                    double probability = Probability(
                        completion.RackHeat,
                        online,
                        state.TotalSlots,
                        rack.Profile.Attenuation,
                        Math.Abs(source.Slot - target.Slot)
                    );
                    if (Roll(state.DamageSeed, completion.Cycle.Key, target.ItemId) < probability)
                    {
                        var condition = ModuleConditionRules.ApplyDamage(target.Condition, source.Damage);
                        modules[target.ItemId] = target with
                        {
                            Condition = condition,
                            Online = target.Online && !condition.BurnedOut,
                            BurnedAt = condition.BurnedOut ? completion.Cycle.CompletesAt : target.BurnedAt,
                        };
                    }
                }
            }
            state = step.State with { Modules = Array.AsReadOnly(modules.Values.OrderBy(m => m.ItemId).ToArray()) };
            state = HeatCycleRules.RetainSources(
                state,
                modules.Values.Where(m => m.Online && !m.Condition.BurnedOut).Select(m => m.ItemId).ToHashSet()
            );
        }
        return HeatCycleRules.Advance(state, now).State;
    }

    // SplitMix64 supplies a stable independent trial for each paid cycle/target.
    // Persisted seed and cycle identity make retries and observer ordering inert.
    private static double Roll(ulong seed, HeatCycleKey cycle, long target)
    {
        ulong value = Mix(seed ^ unchecked((ulong)cycle.ModuleId));
        value = Mix(value ^ unchecked((ulong)cycle.StartedAt));
        value = Mix(value ^ unchecked((ulong)cycle.CompletesAt));
        value = Mix(value ^ unchecked((ulong)target));
        return (value >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
