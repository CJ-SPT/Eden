using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Thermal;

public sealed record HeatCycleKey(long ModuleId, long StartedAt, long CompletesAt);

// Registered only after the owning operation has paid for this exact cycle.
// Stopping automatic repetition does not create another contribution.
public sealed record PaidHeatCycle(long ModuleId, ModuleRack Rack, long StartedAt, long CompletesAt, double Absorption)
{
    public HeatCycleKey Key => new(ModuleId, StartedAt, CompletesAt);
}

public sealed record HeatRackSnapshot(HeatRackProfile Profile, double Heat);

public sealed record ShipHeatSnapshot(long UpdatedAt, IReadOnlyList<HeatRackSnapshot> Racks, IReadOnlyList<PaidHeatCycle> Cycles)
{
    public IReadOnlyList<HeatDamageModule> Modules { get; init; } = [];
    public int TotalSlots { get; init; }
    public ulong DamageSeed { get; init; }

    public bool Idle => Cycles.Count == 0 && Racks.All(r => r.Heat == 0);

    public double Heat(ModuleRack rack) => Racks.SingleOrDefault(r => r.Profile.Rack == rack)?.Heat ?? 0;
}

public sealed record HeatCycleCompletion(PaidHeatCycle Cycle, double RackHeat);

public sealed record HeatTransition(ShipHeatSnapshot State, IReadOnlyList<HeatCycleCompletion> Completed);

public static class HeatCycleRules
{
    private const int HeatRackCount = 3;

    public static bool SameState(ShipHeatSnapshot left, ShipHeatSnapshot right) =>
        left.UpdatedAt == right.UpdatedAt
        && left.Racks.SequenceEqual(right.Racks)
        && left.Cycles.SequenceEqual(right.Cycles)
        && left.Modules.SequenceEqual(right.Modules)
        && left.TotalSlots == right.TotalSlots
        && left.DamageSeed == right.DamageSeed;

    public static ShipHeatSnapshot Create(IReadOnlyList<HeatRackProfile> profiles, long now) =>
        Restore(new(now, profiles.Select(p => new HeatRackSnapshot(p, 0)).ToArray(), []));

    public static ShipHeatSnapshot Restore(ShipHeatSnapshot state)
    {
        Validate(state);
        return state with
        {
            Racks = Array.AsReadOnly(state.Racks.ToArray()),
            Cycles = Array.AsReadOnly(state.Cycles.ToArray()),
            Modules = Array.AsReadOnly(state.Modules.ToArray()),
        };
    }

    public static HeatTransition Start(ShipHeatSnapshot state, PaidHeatCycle cycle)
    {
        Validate(state);
        ValidateCycle(cycle);
        if (state.Cycles.SingleOrDefault(c => c.ModuleId == cycle.ModuleId) == cycle)
        {
            return new(state, []);
        }
        if (cycle.StartedAt < state.UpdatedAt || state.Racks.All(r => r.Profile.Rack != cycle.Rack))
        {
            throw new InvalidDataException("A paid heat contribution cannot start in the past or in an absent rack.");
        }
        var advanced = Advance(state, cycle.StartedAt);
        if (advanced.State.Cycles.Any(c => c.ModuleId == cycle.ModuleId))
        {
            throw new InvalidDataException("A module already has a paid heat contribution.");
        }
        var next = advanced.State with
        {
            UpdatedAt = cycle.StartedAt,
            Cycles = Array.AsReadOnly(advanced.State.Cycles.Append(cycle).ToArray()),
        };
        Validate(next);
        return new(next, advanced.Completed);
    }

    public static HeatTransition Cancel(ShipHeatSnapshot state, HeatCycleKey expected, long now)
    {
        if (expected.ModuleId <= 0 || expected.StartedAt < 0 || expected.CompletesAt <= expected.StartedAt)
        {
            throw new InvalidDataException("Invalid paid heat cancellation key.");
        }
        var advanced = Advance(state, now);
        var cycles = advanced.State.Cycles.Where(c => c.Key != expected).ToArray();
        return cycles.Length == advanced.State.Cycles.Count
            ? advanced
            : advanced with
            {
                State = Advance(advanced.State with { Cycles = Array.AsReadOnly(cycles) }, now).State,
            };
    }

    public static ShipHeatSnapshot RetainSources(ShipHeatSnapshot state, IReadOnlySet<long> available)
    {
        Validate(state);
        var cycles = state.Cycles.Where(c => available.Contains(c.ModuleId)).ToArray();
        return cycles.Length == state.Cycles.Count
            ? state
            : Advance(state with { Cycles = Array.AsReadOnly(cycles) }, state.UpdatedAt).State;
    }

    public static HeatTransition Configure(ShipHeatSnapshot state, IReadOnlyList<HeatRackProfile> profiles, long now)
    {
        var incoming = Create(profiles, now);
        var advanced = Advance(state, now);
        var replacements = incoming.Racks.ToDictionary(r => r.Profile.Rack);
        // A removed physical rack retains its last cooling profile and heat.
        // Fitting changes must not silently heal an already hot hull.
        var racks = advanced
            .State.Racks.Select(r =>
                replacements.TryGetValue(r.Profile.Rack, out var next) ? new HeatRackSnapshot(next.Profile, r.Heat) : r
            )
            .Concat(incoming.Racks.Where(r => advanced.State.Racks.All(old => old.Profile.Rack != r.Profile.Rack)))
            .ToArray();
        return advanced with { State = advanced.State with { Racks = Array.AsReadOnly(racks) } };
    }

    public static HeatTransition Advance(ShipHeatSnapshot state, long now)
    {
        Validate(state);
        if (now < 0)
        {
            throw new InvalidDataException("Invalid heat clock.");
        }
        bool resetsCooling = state.Racks.Any(r =>
            r.Heat > 0
            && r.Heat < HeatRules.CoolingCutoff
            && state.Cycles.Where(c => c.Rack == r.Profile.Rack).Sum(c => c.Absorption) < HeatRules.MinimumIncomingHeat
        );
        if (now < state.UpdatedAt || state.Idle || now == state.UpdatedAt && !resetsCooling)
        {
            return new(state, []);
        }
        long at = state.UpdatedAt;
        var racks = state.Racks.ToArray();
        var cycles = state.Cycles.ToList();
        List<HeatCycleCompletion> completed = [];
        foreach (long deadline in state.Cycles.Where(c => c.CompletesAt <= now).Select(c => c.CompletesAt).Distinct().Order())
        {
            Move(deadline);
            // All completions at one timestamp see the same physical rack heat.
            // Damage settlement owns any subsequent module/burnout ordering.
            foreach (var cycle in cycles.Where(c => c.CompletesAt == deadline).OrderBy(c => c.ModuleId))
            {
                completed.Add(new(cycle, racks.Single(r => r.Profile.Rack == cycle.Rack).Heat));
            }
            cycles.RemoveAll(c => c.CompletesAt == deadline);
        }
        Move(now);
        return new(
            state with
            {
                UpdatedAt = now,
                Racks = Array.AsReadOnly(racks),
                Cycles = Array.AsReadOnly(cycles.ToArray()),
            },
            Array.AsReadOnly(completed.ToArray())
        );

        void Move(long target)
        {
            double elapsed = (target - at) / (double)TimeSpan.TicksPerMillisecond;
            for (int index = 0; index < racks.Length; index++)
            {
                var rack = racks[index];
                double incoming = cycles.Where(c => c.Rack == rack.Profile.Rack).Sum(c => c.Absorption);
                racks[index] = rack with { Heat = HeatRules.Calculate(rack.Profile, rack.Heat, incoming, elapsed) };
            }
            at = target;
        }
    }

    // Damage consumers stop at this boundary, settle the completions, remove
    // burned-out sources, then take another step. Do not heat a dead source
    // through the remainder of a late frame before applying its interruption.
    public static HeatTransition AdvanceToNextDeadline(ShipHeatSnapshot state, long now)
    {
        long deadline = state.Cycles.Count == 0 ? now : state.Cycles.Min(c => c.CompletesAt);
        return Advance(state, Math.Min(now, deadline));
    }

    private static void Validate(ShipHeatSnapshot state)
    {
        if (
            state.UpdatedAt < 0
            || state.Racks.Count > HeatRackCount
            || state.Racks.Select(r => r.Profile.Rack).Distinct().Count() != state.Racks.Count
            || state.Cycles.Count > InventoryDefinitions.SlotsPerRack * HeatRackCount
            || state.Cycles.GroupBy(c => c.Rack).Any(g => g.Count() > InventoryDefinitions.SlotsPerRack)
            || state.Cycles.Select(c => c.ModuleId).Distinct().Count() != state.Cycles.Count
        )
        {
            throw new InvalidDataException("Invalid ship heat state.");
        }
        if (
            state.Modules.Count > InventoryDefinitions.SlotsPerRack * HeatRackCount
            || state.Modules.Select(m => m.ItemId).Distinct().Count() != state.Modules.Count
            || state.TotalSlots < 0
        )
        {
            throw new InvalidDataException("Invalid heat module state.");
        }
        foreach (var module in state.Modules)
        {
            _ = ModuleConditionRules.Define(module.ItemId, module.Condition.HitPoints, module.Condition.Damage);
            if (
                module.ItemId != module.Condition.ItemId
                || module.Slot is < 0 or >= InventoryDefinitions.SlotsPerRack
                || module.Rack is not (ModuleRack.High or ModuleRack.Medium or ModuleRack.Low)
                || !double.IsFinite(module.Damage)
                || module.Damage < 0
            )
            {
                throw new InvalidDataException("Invalid heat module profile.");
            }
        }
        foreach (var rack in state.Racks)
        {
            _ = HeatRules.Calculate(rack.Profile, rack.Heat, 0, 0);
        }
        foreach (var cycle in state.Cycles)
        {
            ValidateCycle(cycle);
            if (
                cycle.StartedAt > state.UpdatedAt
                || cycle.CompletesAt <= state.UpdatedAt
                || state.Racks.All(r => r.Profile.Rack != cycle.Rack)
            )
            {
                throw new InvalidDataException("Invalid paid heat contribution timeline.");
            }
        }
        if (state.Cycles.GroupBy(c => c.Rack).Any(g => !double.IsFinite(g.Sum(c => c.Absorption))))
        {
            throw new InvalidDataException("Paid heat contributions overflow their rack rate.");
        }
    }

    private static void ValidateCycle(PaidHeatCycle cycle)
    {
        if (
            cycle.ModuleId <= 0
            || cycle.Rack is not (ModuleRack.Low or ModuleRack.Medium or ModuleRack.High)
            || cycle.StartedAt < 0
            || cycle.CompletesAt <= cycle.StartedAt
            || !double.IsFinite(cycle.Absorption)
            || cycle.Absorption < 0
        )
        {
            throw new InvalidDataException("Invalid paid heat contribution.");
        }
    }
}
