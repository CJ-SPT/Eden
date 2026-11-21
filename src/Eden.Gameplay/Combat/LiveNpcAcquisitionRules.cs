using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public sealed record LiveNpcTarget(long Id, int SystemId, SpacePosition Position, double Radius);

public static class LiveNpcAcquisitionRules
{
    public static IReadOnlyList<LiveAggression> Acquire(
        IReadOnlyList<LiveCombatNpc> npcs,
        IReadOnlyList<LiveNpcTarget> targets,
        IReadOnlySet<long> occupied,
        long now
    ) => Acquire(npcs, targets, occupied.Contains, now);

    public static IReadOnlyList<LiveAggression> Acquire(
        IReadOnlyList<LiveCombatNpc> npcs,
        IReadOnlyList<LiveNpcTarget> targets,
        Func<long, bool> occupied,
        long now
    )
    {
        if (npcs.Count == 0 || targets.Count == 0)
        {
            return [];
        }
        List<LiveAggression>? additions = null;
        foreach (var npc in npcs.OrderBy(n => n.Id))
        {
            if (!TryReactionDelay(npc, occupied, out double delay))
            {
                continue;
            }
            var (maximumRange, leash) = Boundaries(npc);
            LiveNpcTarget? target = null;
            double closest = double.PositiveInfinity;
            foreach (var ship in targets)
            {
                if (!TryTargetRange(npc, ship, out double range))
                {
                    continue;
                }
                if (
                    range <= maximumRange
                    && (target is null || range < closest || range == closest && ship.Id < target.Id)
                    && ship.Position.DistanceTo(npc.Home) <= leash
                )
                {
                    target = ship;
                    closest = range;
                }
            }
            if (target is not null)
            {
                (additions ??= []).Add(Start(npc.Id, target.Id, delay, now));
            }
        }
        return additions is null ? [] : additions;
    }

    // The resident system membership already supplies NPCs in ID order.
    public static IReadOnlyList<LiveAggression> AcquireOrdered(
        IReadOnlyList<LiveCombatNpc> orderedNpcs,
        LiveNpcTarget target,
        Func<long, bool> occupied,
        long now
    )
    {
        List<LiveAggression>? additions = null;
        for (int index = 0; index < orderedNpcs.Count; index++)
        {
            var npc = orderedNpcs[index];
            if (!TryReactionDelay(npc, occupied, out double delay))
            {
                continue;
            }
            var (maximumRange, leash) = Boundaries(npc);
            if (TryTargetRange(npc, target, out double range) && range <= maximumRange && target.Position.DistanceTo(npc.Home) <= leash)
            {
                (additions ??= []).Add(Start(npc.Id, target.Id, delay, now));
            }
        }
        return additions is null ? [] : additions;
    }

    private static bool TryReactionDelay(LiveCombatNpc npc, Func<long, bool> occupied, out double delay)
    {
        delay = 0;
        return npc.Role == NpcRole.Pirate
            && npc.Navigation == NpcNavigationState.Idle
            && !occupied(npc.Id)
            && (
                npc.Attributes.TryGetValue(DogmaAttributeId.EntityAttackDelayMinimum, out delay)
                || npc.GroupId is NpcEncounterDefinitions.SentryGroup or NpcEncounterDefinitions.OverseerSentryGroup
            );
    }

    private static (double Maximum, double Leash) Boundaries(LiveCombatNpc npc)
    {
        double detection = NpcPopulationRules.DetectionRange(npc.Attributes, npc.MissileRange, npc.AcquisitionRange);
        double maximum = Math.Min(detection, npc.Attributes.GetValueOrDefault(DogmaAttributeId.MaxTargetRange, double.MaxValue));
        return (maximum, NpcPopulationRules.Leash(detection));
    }

    private static bool TryTargetRange(LiveCombatNpc npc, LiveNpcTarget target, out double range)
    {
        if (target.SystemId != npc.SystemId || !PlayerEwarRules.CanTarget(npc.IncomingElectronicWarfare, target.Id))
        {
            range = 0;
            return false;
        }
        range = Math.Max(0, target.Position.DistanceTo(npc.Position) - npc.Radius - target.Radius);
        return true;
    }

    private static LiveAggression Start(long npc, long target, double delay, long now)
    {
        if (!double.IsFinite(delay) || delay < 0)
        {
            throw new InvalidDataException("Invalid NPC reaction delay.");
        }
        long ready = checked(now + (long)Math.Ceiling(delay * TimeSpan.TicksPerMillisecond));
        return new(npc, target, ready, ready, null);
    }
}
